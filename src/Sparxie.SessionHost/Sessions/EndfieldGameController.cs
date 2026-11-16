using System.Diagnostics;
using System.Runtime.InteropServices;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Endfield;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// Endfield 启动与运行时控制器。EndfieldCore 以挂起状态创建 Endfield.exe 并加入会话 Job，
/// 随后注入独立 Runtime，由 Runtime 通过公开的 IL2CPP 导出调用输入模式切换与 Unity 帧率接口。
/// </summary>
public sealed class EndfieldGameController : IGameController
{
    private const int Ok = 0;
    private IntPtr _session;
    private string? _launchPath;
    private string _profileId = string.Empty;
    private EndfieldUiSync? _uiSync;
    private bool _disposed;
    private readonly string _iniPath;
    private readonly string _configPath;

    public EndfieldGameController()
        : this(EndfieldUiIni.DefaultPath(), Path.Combine(AppDataDirectory.Resolve(), "config.json"))
    {
    }

    /// <summary>Tests point the ini and the config at their own folder.</summary>
    internal EndfieldGameController(string iniPath, string configPath)
    {
        _iniPath = iniPath;
        _configPath = configPath;
    }

    public bool CreatesProcess => true;

    public Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken)
    {
        _launchPath = profile.ExecutablePath;
        _profileId = profile.ProfileId;
        return Task.CompletedTask;
    }

    public Task InstallAsync(Process? gameProcess, IntPtr jobHandle, HoyoProfileSettings? hoyo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_launchPath))
        {
            throw new InvalidOperationException("Endfield EXE 路径为空");
        }

        // touch-interface settings reach the Runtime through endfield-ui.ini; they are in place before the game exists, so its first read already sees them
        _uiSync = new EndfieldUiSync(_profileId, _iniPath, _configPath, WriteLogLine);
        _uiSync.Write(hoyo);

        var request = BuildRequest(_launchPath, hoyo);
        var result = NewResult();
        try
        {
            var rc = endfield_create_session(ref request, ref result, out _session);
            ThrowIfFailed(rc, result, "Endfield 会话创建失败");

            if (jobHandle != IntPtr.Zero)
            {
                result = NewResult();
                rc = endfield_attach_job(_session, jobHandle, ref result);
                ThrowIfFailed(rc, result, "Endfield Job 绑定失败");
            }

            result = NewResult();
            rc = endfield_launch(_session, ref result);
            ThrowIfFailed(rc, result, $"Endfield 启动注入失败（阶段 {result.Stage}）");
            _uiSync.Start(UiFollowInterval);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }

        return Task.CompletedTask;
    }

    public Task PostInstallAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AbortAsync(CancellationToken cancellationToken)
    {
        Release();
        return Task.CompletedTask;
    }

    public async Task WaitExitAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = NewResult();
            var rc = endfield_wait_game_exit(_session, 500, ref result);
            if (rc == Ok)
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken)
    {
        var result = NewResult();
        var rc = endfield_set_target_fps(_session, targetFps, ref result);
        ThrowIfFailed(rc, result, "Endfield FPS 热调失败");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }

    private static readonly TimeSpan UiFollowInterval = TimeSpan.FromSeconds(2);

    /// <summary>One timestamped line into logs\endfield-launch.log. A diagnostics line must never fail a launch.</summary>
    private static void WriteLogLine(string text)
    {
        try
        {
            var directory = Path.Combine(AppDataDirectory.Resolve(), "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "endfield-launch.log"), $"[{DateTime.Now:O}] {text}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // see the summary
        }
    }

    private void Release()
    {
        _uiSync?.Dispose();
        _uiSync = null;
        if (_session != IntPtr.Zero)
        {
            endfield_release(_session);
            _session = IntPtr.Zero;
        }
    }

    private static EndfieldResult NewResult() => new() { Size = (uint)Marshal.SizeOf<EndfieldResult>() };

    internal static EndfieldLaunchRequest BuildRequest(string launchPath, HoyoProfileSettings? hoyo)
    {
        var settings = hoyo ?? new HoyoProfileSettings();
        var width = settings.EndfieldAutoResolution ? GetSystemMetrics(0) : settings.EndfieldWidth;
        var height = settings.EndfieldAutoResolution ? GetSystemMetrics(1) : settings.EndfieldHeight;
        return new EndfieldLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<EndfieldLaunchRequest>(),
            AbiVersion = AbiVersion,
            FpsUnlockEnabled = settings.FpsUnlockEnabled ? 1 : 0,
            TargetFps = settings.TargetFps,
            TouchUiEnabled = settings.EndfieldInputMode is "pc" ? 0 : 1,
            Width = width > 0 ? width : settings.EndfieldWidth,
            Height = height > 0 ? height : settings.EndfieldHeight,
            RefreshRate = settings.EndfieldRefreshRate,
            GameExecutablePath = Marshal.StringToHGlobalUni(launchPath),
            GameExecutablePathChars = (uint)launchPath.Length,
        };
    }

    private static void ThrowIfFailed(int code, EndfieldResult result, string prefix)
    {
        if (code == Ok)
        {
            return;
        }

        var detail = Marshal.PtrToStringUni(result.Message);
        throw new InvalidOperationException($"{prefix}: {code} {detail}");
    }

    private const uint AbiVersion = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    internal struct EndfieldLaunchRequest
    {
        public uint Size;
        public uint AbiVersion;
        public int FpsUnlockEnabled;
        public int TargetFps;
        public int TouchUiEnabled;
        public int Width;
        public int Height;
        public int RefreshRate;
        public IntPtr GameExecutablePath;
        public uint GameExecutablePathChars;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EndfieldResult
    {
        public uint Size;
        public int ErrorCode;
        public uint Stage;
        public uint Detail;
        public IntPtr Message;
        public uint MessageChars;
    }

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_create_session(ref EndfieldLaunchRequest request, ref EndfieldResult result, out IntPtr session);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_attach_job(IntPtr session, IntPtr jobHandle, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_launch(IntPtr session, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_set_target_fps(IntPtr session, int targetFps, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_wait_game_exit(IntPtr session, uint timeoutMs, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_release(IntPtr session);
}
