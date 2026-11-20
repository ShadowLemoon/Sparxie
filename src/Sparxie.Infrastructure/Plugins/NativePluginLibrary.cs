using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sparxie.Infrastructure.Plugins;

public sealed class PluginLoadException(string message) : Exception(message);

/// <summary>模块调用返回非 SPX_OK。</summary>
public sealed class PluginCallException(string function, int code, string message)
    : Exception($"插件 {function} 失败: {code} {message}")
{
    public int Code { get; } = code;
}

/// <summary>
/// 已加载的插件 DLL：持有 DLL 句柄（FILE_SHARE_READ，加载期间无法被改写）并重算哈希，
/// 用 get_info 与 plugin.json 交叉核对。从不 FreeLibrary（Hook 仍挂着时卸载不安全，退出进程即卸载）。
/// </summary>
public sealed class NativePluginLibrary : IDisposable
{
    public const int SpxOk = 0;
    public const int SpxErrTimeout = 101;
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private readonly FileStream _dllLock;
    private readonly Api _api;

    private NativePluginLibrary(PluginEntry entry, FileStream dllLock, Api api)
    {
        Entry = entry;
        _dllLock = dllLock;
        _api = api;
    }

    public PluginEntry Entry { get; }

    internal Api Functions => _api;

    /// <summary>加载 Ready 状态的插件。调用方应先重新扫描目录，保证 entry 是最新的。</summary>
    public static NativePluginLibrary Load(PluginEntry entry)
    {
        if (entry.Availability != PluginAvailability.Ready || entry.Manifest is null)
        {
            throw new PluginLoadException($"插件 {entry.Id} 不是 Ready 状态: {entry.Availability}");
        }

        var dllPath = Path.Combine(entry.Directory, PluginManifest.ModuleFileName);
        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(lockStream)).ToLowerInvariant();
            if (hash != entry.DllSha256)
            {
                throw new PluginLoadException("插件 DLL 与确认时的哈希不一致");
            }

            var module = LoadLibraryExW(dllPath, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero)
            {
                throw new PluginLoadException($"LoadLibrary 失败: {Marshal.GetLastWin32Error()}");
            }

            var query = GetProcAddress(module, "sparxie_module_query");
            if (query == IntPtr.Zero)
            {
                throw new PluginLoadException("缺少导出 sparxie_module_query");
            }

            var queryFn = Marshal.GetDelegateForFunctionPointer<QueryFn>(query);
            var rc = queryFn(PluginManifest.SupportedAbiMajor, 0, out var apiPtr);
            if (rc != SpxOk || apiPtr == IntPtr.Zero)
            {
                throw new PluginLoadException($"sparxie_module_query 返回 {rc}");
            }

            var api = Api.Read(apiPtr);
            CrossCheck(entry.Manifest, api);
            return new NativePluginLibrary(entry, lockStream, api);
        }
        catch
        {
            lockStream?.Dispose();
            throw;
        }
    }

    public void Dispose() => _dllLock.Dispose();

    private static void CrossCheck(PluginManifest manifest, Api api)
    {
        if (api.AbiMajor != PluginManifest.SupportedAbiMajor)
        {
            throw new PluginLoadException($"插件 ABI 主版本 {api.AbiMajor} 不受支持");
        }

        var rc = api.GetInfo(out var infoPtr);
        if (rc != SpxOk || infoPtr == IntPtr.Zero)
        {
            throw new PluginLoadException($"get_info 返回 {rc}");
        }

        var info = Marshal.PtrToStructure<SpxModuleInfo>(infoPtr);
        if (info.AbiMajor != PluginManifest.SupportedAbiMajor)
        {
            throw new PluginLoadException("get_info 的 ABI 主版本与宿主不符");
        }

        if (ReadStr(info.Id) != manifest.Id)
        {
            throw new PluginLoadException("get_info 的 id 与 plugin.json 不一致");
        }

        var exes = new List<string>();
        for (var i = 0; i < info.ExeCount && i < 16; i++)
        {
            exes.Add(ReadStr(Marshal.PtrToStructure<SpxStr>(info.ExeNames + (i * Marshal.SizeOf<SpxStr>()))));
        }

        if (!exes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                manifest.ExeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            throw new PluginLoadException("get_info 的 EXE 列表与 plugin.json 不一致");
        }

        if (info.Caps != CapsFromManifest(manifest))
        {
            throw new PluginLoadException("get_info 的能力位与 plugin.json 不一致");
        }
    }

    public static uint CapsFromManifest(PluginManifest manifest)
    {
        uint caps = 0;
        if (manifest.HasCap("createsProcess")) { caps |= 1; }
        if (manifest.HasCap("injects")) { caps |= 2; }
        if (manifest.HasCap("needsArgs")) { caps |= 4; }
        if (manifest.HasCap("hotParams")) { caps |= 8; }
        return caps;
    }

    internal static string ReadStr(SpxStr value) =>
        value.P == IntPtr.Zero || value.Chars == 0 || value.Chars > 4096 ? string.Empty : Marshal.PtrToStringUni(value.P, (int)value.Chars);

    internal static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(message.Length, 512));
        foreach (var ch in message.Take(512))
        {
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString().Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpxStr
    {
        public IntPtr P;
        public uint Chars;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SpxResult
    {
        public uint Size;
        public int ErrorCode;
        public uint Stage;
        public uint Detail;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string Message;

        public static SpxResult New() => new() { Size = (uint)Marshal.SizeOf<SpxResult>(), Message = string.Empty };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpxModuleInfo
    {
        public uint Size;
        public uint AbiMajor;
        public uint AbiMinor;
        public SpxStr Id;
        public SpxStr DisplayName;
        public SpxStr Version;
        public uint Caps;
        public IntPtr ExeNames;
        public uint ExeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpxLaunchRequest
    {
        public uint Size;
        public SpxStr ExePath;
        public SpxStr Arguments;
        public SpxStr SettingsJson;
        public SpxStr LogDir;
        public SpxStr DataDir;
        public IntPtr Host;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpxHostApi
    {
        public uint Size;
        public uint AbiMajor;
        public uint AbiMinor;
        public IntPtr Context;
        public IntPtr RegistrySetDword;
        public IntPtr RegistrySetString;
        public IntPtr RegistryDeleteValue;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int RegistrySetDwordFn(IntPtr context, SpxStr key, SpxStr name, uint value, IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int RegistrySetStringFn(IntPtr context, SpxStr key, SpxStr name, SpxStr value, IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int RegistryDeleteValueFn(IntPtr context, SpxStr key, SpxStr name, IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct ApiRaw
    {
        public uint Size;
        public uint AbiMajor;
        public uint AbiMinor;
        public IntPtr GetInfo;
        public IntPtr CreateSession;
        public IntPtr PrepareLaunch;
        public IntPtr AttachJob;
        public IntPtr Install;
        public IntPtr PostInstall;
        public IntPtr WaitExit;
        public IntPtr SetParam;
        public IntPtr Abort;
        public IntPtr Release;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryFn(uint hostMajor, uint hostMinor, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetInfoFn(out IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreateSessionFn(ref SpxLaunchRequest request, ref SpxResult result, out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SessionFn(IntPtr session, ref SpxResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int AttachJobFn(IntPtr session, IntPtr job, ref SpxResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InstallFn(IntPtr session, uint pid, ref SpxResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int WaitExitFn(IntPtr session, uint timeoutMs, ref SpxResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SetParamFn(IntPtr session, SpxStr key, long value, ref SpxResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReleaseFn(IntPtr session);

    internal sealed class Api
    {
        public uint AbiMajor { get; private init; }

        public GetInfoFn GetInfo { get; private init; } = null!;

        public CreateSessionFn CreateSession { get; private init; } = null!;

        public SessionFn PrepareLaunch { get; private init; } = null!;

        public AttachJobFn AttachJob { get; private init; } = null!;

        public InstallFn Install { get; private init; } = null!;

        public SessionFn PostInstall { get; private init; } = null!;

        public WaitExitFn WaitExit { get; private init; } = null!;

        public SetParamFn SetParam { get; private init; } = null!;

        public SessionFn Abort { get; private init; } = null!;

        public ReleaseFn Release { get; private init; } = null!;

        public static Api Read(IntPtr pointer)
        {
            var raw = Marshal.PtrToStructure<ApiRaw>(pointer);
            if (raw.Size < Marshal.SizeOf<ApiRaw>())
            {
                throw new PluginLoadException("插件函数表过小（size 不足）");
            }

            T Bind<T>(IntPtr fn, string name) where T : Delegate =>
                fn == IntPtr.Zero ? throw new PluginLoadException($"函数表缺少 {name}") : Marshal.GetDelegateForFunctionPointer<T>(fn);

            return new Api
            {
                AbiMajor = raw.AbiMajor,
                GetInfo = Bind<GetInfoFn>(raw.GetInfo, "get_info"),
                CreateSession = Bind<CreateSessionFn>(raw.CreateSession, "create_session"),
                PrepareLaunch = Bind<SessionFn>(raw.PrepareLaunch, "prepare_launch"),
                AttachJob = Bind<AttachJobFn>(raw.AttachJob, "attach_job"),
                Install = Bind<InstallFn>(raw.Install, "install"),
                PostInstall = Bind<SessionFn>(raw.PostInstall, "post_install"),
                WaitExit = Bind<WaitExitFn>(raw.WaitExit, "wait_exit"),
                SetParam = Bind<SetParamFn>(raw.SetParam, "set_param"),
                Abort = Bind<SessionFn>(raw.Abort, "abort"),
                Release = Bind<ReleaseFn>(raw.Release, "release"),
            };
        }
    }
}

/// <summary>
/// 插件会话：同一会话的所有 ABI 调用都在同一个专用线程上执行（Hook 归调用线程所有）。
/// 每次调用有超时；超时后线程被放弃（进程退出时回收），调用方应当按失败处理。
/// </summary>
public sealed class NativePluginSession : IDisposable
{
    private readonly NativePluginLibrary _library;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private IntPtr _session;
    private bool _released;
    private IntPtr _hostApi;
    private readonly List<Delegate> _hostDelegates = [];

    public NativePluginSession(NativePluginLibrary library)
    {
        _library = library;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"plugin-{library.Entry.Id}" };
        _thread.Start();
    }

    public bool CreatesProcess => _library.Entry.Manifest!.HasCap("createsProcess");

    public Task CreateAsync(string exePath, string arguments, string settingsJson, string logDir, string dataDir, TimeSpan timeout, RegistryHookSession? registryHooks = null) =>
        RunAsync("create_session", timeout, () =>
        {
            var handles = new List<IntPtr>();
            try
            {
                IntPtr Alloc(string value, out uint chars)
                {
                    chars = (uint)value.Length;
                    var ptr = Marshal.StringToHGlobalUni(value);
                    handles.Add(ptr);
                    return ptr;
                }

                NativePluginLibrary.SpxStr Str(string value) => new() { P = Alloc(value, out var chars), Chars = chars };

                var request = new NativePluginLibrary.SpxLaunchRequest
                {
                    Size = (uint)Marshal.SizeOf<NativePluginLibrary.SpxLaunchRequest>(),
                    ExePath = Str(exePath),
                    Arguments = Str(arguments),
                    SettingsJson = Str(settingsJson),
                    LogDir = Str(logDir),
                    DataDir = Str(dataDir),
                    Host = registryHooks is null ? IntPtr.Zero : BuildHostApi(registryHooks),
                };
                var result = NativePluginLibrary.SpxResult.New();
                var rc = _library.Functions.CreateSession(ref request, ref result, out var session);
                Check("create_session", rc, result);
                _session = session;
            }
            finally
            {
                handles.ForEach(Marshal.FreeHGlobal);
            }
        });

    public Task PrepareLaunchAsync(TimeSpan timeout) =>
        RunAsync("prepare_launch", timeout, () => Call("prepare_launch", _library.Functions.PrepareLaunch));

    public Task AttachJobAsync(IntPtr job, TimeSpan timeout) =>
        RunAsync("attach_job", timeout, () =>
        {
            var result = NativePluginLibrary.SpxResult.New();
            Check("attach_job", _library.Functions.AttachJob(_session, job, ref result), result);
        });

    public Task InstallAsync(uint gamePid, TimeSpan timeout) =>
        RunAsync("install", timeout, () =>
        {
            var result = NativePluginLibrary.SpxResult.New();
            Check("install", _library.Functions.Install(_session, gamePid, ref result), result);
        });

    public Task PostInstallAsync(TimeSpan timeout) =>
        RunAsync("post_install", timeout, () => Call("post_install", _library.Functions.PostInstall));

    public Task AbortAsync(TimeSpan timeout) =>
        RunAsync("abort", timeout, () => Call("abort", _library.Functions.Abort));

    /// <summary>等待游戏退出：true 表示已退出，false 表示本轮超时。</summary>
    public Task<bool> WaitExitAsync(uint timeoutMs, TimeSpan callTimeout) =>
        RunAsync("wait_exit", callTimeout, () =>
        {
            var result = NativePluginLibrary.SpxResult.New();
            var rc = _library.Functions.WaitExit(_session, timeoutMs, ref result);
            if (rc == NativePluginLibrary.SpxErrTimeout)
            {
                return false;
            }

            Check("wait_exit", rc, result);
            return true;
        });

    public Task SetParamAsync(string key, long value, TimeSpan timeout) =>
        RunAsync("set_param", timeout, () =>
        {
            var keyPtr = Marshal.StringToHGlobalUni(key);
            try
            {
                var result = NativePluginLibrary.SpxResult.New();
                var str = new NativePluginLibrary.SpxStr { P = keyPtr, Chars = (uint)key.Length };
                Check("set_param", _library.Functions.SetParam(_session, str, value, ref result), result);
            }
            finally
            {
                Marshal.FreeHGlobal(keyPtr);
            }
        });

    /// <summary>
    /// 幂等释放。release 一定排进专用线程的队列（即使 create_session 超时、当时还没有会话——排在它后面执行时会话已建好），
    /// 宿主回调表和委托只在 release 真正执行之后才释放；5 秒内没等到（线程卡在插件里）就**有意泄漏**并长期保留，
    /// 因为插件仍可能在那个线程上回调宿主，提前释放会变成访问已释放内存。
    /// </summary>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        var done = new ManualResetEventSlim();
        var enqueued = false;
        try
        {
            if (_thread.IsAlive)
            {
                _queue.Add(() =>
                {
                    try
                    {
                        if (_session != IntPtr.Zero)
                        {
                            _library.Functions.Release(_session);
                            _session = IntPtr.Zero;
                        }

                        FreeHostApi();
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                enqueued = true;
            }
        }
        catch (InvalidOperationException)
        {
            // 队列已关闭
        }
        finally
        {
            _queue.CompleteAdding();
        }

        if (!enqueued)
        {
            FreeHostApi(); // 线程已经结束，没有谁会再回调
        }
        else if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            Retained.Add(this); // 线程卡住：保留委托与回调表，直到进程退出
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentBag<NativePluginSession> Retained = [];

    private void FreeHostApi()
    {
        if (_hostApi != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_hostApi);
            _hostApi = IntPtr.Zero;
        }

        _hostDelegates.Clear();
    }

    /// <summary>构造传给插件的宿主服务表：注册表回调全部经过 <see cref="RegistryHookSession"/> 的前缀校验与备份。</summary>
    private IntPtr BuildHostApi(RegistryHookSession hooks)
    {
        // 回调来自不受信任的原生代码：result 可能为 NULL、字符串可能超长或指针非法、任何异常都不能穿过原生栈帧。
        NativePluginLibrary.RegistrySetDwordFn setDword = (IntPtr _, NativePluginLibrary.SpxStr key, NativePluginLibrary.SpxStr name, uint value, IntPtr result) =>
            Guard(result, () =>
                TryRead(key, RegistryHookSession.MaxKeyLength, out var k) && TryRead(name, RegistryHookSession.MaxNameLength, out var n)
                    ? hooks.SetDword(k, n, value)
                    : (false, "参数过长或无效"));
        NativePluginLibrary.RegistrySetStringFn setString = (IntPtr _, NativePluginLibrary.SpxStr key, NativePluginLibrary.SpxStr name, NativePluginLibrary.SpxStr value, IntPtr result) =>
            Guard(result, () =>
                TryRead(key, RegistryHookSession.MaxKeyLength, out var k) && TryRead(name, RegistryHookSession.MaxNameLength, out var n)
                    && TryRead(value, RegistryHookSession.MaxStringLength, out var v)
                    ? hooks.SetString(k, n, v)
                    : (false, "参数过长或无效"));
        NativePluginLibrary.RegistryDeleteValueFn deleteValue = (IntPtr _, NativePluginLibrary.SpxStr key, NativePluginLibrary.SpxStr name, IntPtr result) =>
            Guard(result, () =>
                TryRead(key, RegistryHookSession.MaxKeyLength, out var k) && TryRead(name, RegistryHookSession.MaxNameLength, out var n)
                    ? hooks.DeleteValue(k, n)
                    : (false, "参数过长或无效"));
        _hostDelegates.AddRange([setDword, setString, deleteValue]);

        var api = new NativePluginLibrary.SpxHostApi
        {
            Size = (uint)Marshal.SizeOf<NativePluginLibrary.SpxHostApi>(),
            AbiMajor = PluginManifest.SupportedAbiMajor,
            AbiMinor = 0,
            Context = IntPtr.Zero,
            RegistrySetDword = Marshal.GetFunctionPointerForDelegate(setDword),
            RegistrySetString = Marshal.GetFunctionPointerForDelegate(setString),
            RegistryDeleteValue = Marshal.GetFunctionPointerForDelegate(deleteValue),
        };
        _hostApi = Marshal.AllocHGlobal(Marshal.SizeOf<NativePluginLibrary.SpxHostApi>());
        Marshal.StructureToPtr(api, _hostApi, fDeleteOld: false);
        return _hostApi;
    }

    /// <summary>严格读取插件传来的字符串：指针为空而长度非零、或超过上限都算失败，绝不截断成空串。</summary>
    internal static bool TryRead(NativePluginLibrary.SpxStr value, int maxChars, out string text)
    {
        text = string.Empty;
        if (value.Chars > (uint)maxChars || (value.Chars > 0 && value.P == IntPtr.Zero))
        {
            return false;
        }

        if (value.Chars > 0)
        {
            text = Marshal.PtrToStringUni(value.P, (int)value.Chars);
        }

        return true;
    }

    /// <summary>
    /// 回调的统一外壳：result 可为 NULL；任何异常都转成 SPX_ERR_INTERNAL 返回，不让异常穿过原生栈帧。
    /// 只按 result 自带的 size 覆盖到的范围回写，不假设整个结构都可写。
    /// </summary>
    internal static int Guard(IntPtr result, Func<(bool Ok, string Message)> action)
    {
        const int InvalidArgument = 1;
        const int Internal = 10;
        try
        {
            var outcome = action();
            if (outcome.Ok)
            {
                return NativePluginLibrary.SpxOk;
            }

            WriteResult(result, InvalidArgument, outcome.Message);
            return InvalidArgument;
        }
        catch (Exception ex)
        {
            try
            {
                WriteResult(result, Internal, ex.Message);
            }
            catch (Exception)
            {
                // 连写错误信息都失败：只返回错误码
            }

            return Internal;
        }
    }

    private static void WriteResult(IntPtr result, int code, string? message)
    {
        if (result == IntPtr.Zero)
        {
            return;
        }

        var size = unchecked((uint)Marshal.ReadInt32(result, 0));
        var errorOffset = (int)Marshal.OffsetOf<NativePluginLibrary.SpxResult>(nameof(NativePluginLibrary.SpxResult.ErrorCode));
        var messageOffset = (int)Marshal.OffsetOf<NativePluginLibrary.SpxResult>(nameof(NativePluginLibrary.SpxResult.Message));
        if (size >= errorOffset + sizeof(int))
        {
            Marshal.WriteInt32(result, errorOffset, code);
        }

        // 消息缓冲固定 512 个 UTF-16 字符（含 NUL）；size 没覆盖到整块缓冲就不写。
        const int MessageChars = 512;
        if (size >= messageOffset + (MessageChars * sizeof(char)))
        {
            var text = NativePluginLibrary.SanitizeMessage(message);
            if (text.Length > MessageChars - 1)
            {
                text = text[..(MessageChars - 1)];
            }

            for (var i = 0; i < text.Length; i++)
            {
                Marshal.WriteInt16(result, messageOffset + (i * sizeof(char)), text[i]);
            }

            Marshal.WriteInt16(result, messageOffset + (text.Length * sizeof(char)), 0);
        }
    }

    private void Call(string name, NativePluginLibrary.SessionFn fn)
    {
        var result = NativePluginLibrary.SpxResult.New();
        Check(name, fn(_session, ref result), result);
    }

    private static void Check(string name, int rc, NativePluginLibrary.SpxResult result)
    {
        if (rc != NativePluginLibrary.SpxOk)
        {
            throw new PluginCallException(name, rc, NativePluginLibrary.SanitizeMessage(result.Message));
        }
    }

    private Task RunAsync(string name, TimeSpan timeout, Action action) =>
        RunAsync<object?>(name, timeout, () =>
        {
            action();
            return null;
        });

    private async Task<T> RunAsync<T>(string name, TimeSpan timeout, Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        try
        {
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"插件 {name} 超过 {timeout.TotalSeconds:0} 秒未返回");
        }
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
