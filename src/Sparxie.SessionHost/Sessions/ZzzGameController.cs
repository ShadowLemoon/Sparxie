using System.Diagnostics;
using System.Runtime.InteropServices;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Zzz;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// 绝区零 Runtime 控制器：启动前备份 PC 配置并写入触屏配置、建立恢复记录；
/// 注入成功后按已验证时序（窗口客户区就绪 + 延迟）恢复 PC 文件配置并删除恢复记录；
/// 失败路径恢复 PC 配置。Running 前 Host 异常死亡时由 Broker 用同一恢复例程兜底。
/// </summary>
public sealed class ZzzGameController : IGameController
{
    public bool CreatesProcess => false;

    private const uint WindowWaitMs = 60_000;
    private const int ClientAreaReadyTimeoutMs = 120_000;
    private const int PostClientAreaDelayMs = 5_000;

    private readonly string _sessionId;
    private ZzzRecoveryRecord? _record;
    private bool _injected;
    private bool _released;

    public ZzzGameController(string sessionId)
    {
        _sessionId = sessionId;
    }

    public async Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken)
    {
        // 1) 处理并校验遗留恢复记录：存在未完成记录时先恢复，避免与旧会话冲突。
        foreach (var pending in ZzzRecoveryStore.FindAll())
        {
            if (string.Equals(pending.SessionId, _sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            ZzzRecoveryStore.Restore(pending);
        }

        // 2) 验证 EXE 与 GENERAL_DATA.bin。
        var dataPath = ResolveGeneralDataPath(profile.ExecutablePath);
        if (!File.Exists(dataPath))
        {
            throw new FileNotFoundException("GENERAL_DATA.bin 不存在", dataPath);
        }

        // 3) 原子备份 PC 配置并写入会话恢复记录。
        _record = ZzzRecoveryStore.Create(_sessionId, dataPath);

        // 4) 写入触屏配置；失败时回滚恢复记录。
        try
        {
            ZzzGeneralData.WritePlatform(dataPath, ZzzGeneralData.PlatformTouch);
        }
        catch
        {
            TryRollbackRecord();
            throw;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task InstallAsync(Process? gameProcess, IntPtr jobHandle, Sparxie.Contracts.Models.HoyoProfileSettings? hoyo, CancellationToken cancellationToken)
    {
        // ZZZ 由 SessionHost 预创建进程（CreatesProcess=false），运行期 gameProcess 非空
        if (gameProcess is null)
        {
            throw new InvalidOperationException("ZZZ 游戏进程未创建");
        }

        TargetPid = (uint)gameProcess.Id;
        var result = ZZZTouchInjectToProcess(TargetPid, quiet: true, WindowWaitMs);
        if (result != 0)
        {
            throw new InvalidOperationException($"ZZZ Runtime 注入失败: {DescribeInjectResult(result)}");
        }

        _injected = true;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task PostInstallAsync(CancellationToken cancellationToken)
    {
        // 已验证时序：等待主窗口客户区就绪（游戏完成初次配置读取），再延迟写回 PC。
        if (_record is null)
        {
            return;
        }

        if (!WaitForClientArea(ClientAreaReadyTimeoutMs, cancellationToken))
        {
            // 客户区超时：保持恢复记录，由失败路径或下次启动兜底恢复。
            throw new TimeoutException("等待游戏主窗口客户区就绪超时");
        }

        await Task.Delay(PostClientAreaDelayMs, cancellationToken).ConfigureAwait(false);

        ZzzRecoveryStore.Restore(_record);
        _record = null;
    }

    public Task AbortAsync(CancellationToken cancellationToken)
    {
        // 失败路径清理：若仍存在恢复记录（游戏已被终止），恢复 PC 配置。
        var record = _record ?? ZzzRecoveryStore.TryLoad(_sessionId);
        if (record is null)
        {
            return Task.CompletedTask;
        }

        ZzzRecoveryStore.Restore(record);
        _record = null;
        return Task.CompletedTask;
    }

    public Task WaitExitAsync(CancellationToken cancellationToken)
        => Task.CompletedTask; // ZZZ 由 SessionHost 预创建并等待

    public Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken)
        => throw new NotSupportedException("绝区零不提供 FPS 热调");

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_injected)
        {
            // 只有注入成功后才有需要释放的 native 会话
            ZZZTouchRelease();
        }
    }

    private void TryRollbackRecord()
    {
        try
        {
            if (_record is not null)
            {
                ZzzRecoveryStore.Delete(_record);
            }
        }
        catch
        {
            // 回滚失败不掩盖原始错误；遗留记录由 Broker/下次启动兜底
        }

        _record = null;
    }

    /// <summary>解析 {exe目录}\*_Data\Persistent\LocalStorage\GENERAL_DATA.bin，要求唯一命中。</summary>
    public static string ResolveGeneralDataPath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("executablePath 为空", nameof(executablePath));
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"游戏目录不存在: {root}");
        }

        var candidates = Directory.GetDirectories(root, "*_Data", SearchOption.TopDirectoryOnly)
            .Select(dataDir => Path.Combine(dataDir, "Persistent", "LocalStorage", "GENERAL_DATA.bin"))
            .Where(File.Exists)
            .ToList();

        return candidates.Count switch
        {
            0 => throw new FileNotFoundException("未找到唯一的 GENERAL_DATA.bin"),
            1 => candidates[0],
            _ => throw new InvalidDataException("找到多个 GENERAL_DATA.bin，无法安全判断所属安装"),
        };
    }

    private static bool WaitForClientArea(int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ready = false;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid != TargetPid)
                {
                    return true;
                }

                if (GetClientRect(hWnd, out var rect) && rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0)
                {
                    ready = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (ready)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static uint TargetPid { get; set; }

    private static string DescribeInjectResult(int result) => result switch
    {
        0 => "成功",
        1 => "主窗口未找到",
        2 => "LoadLibrary 失败",
        3 => "GetProcAddress 失败",
        4 => "OpenProcess 失败",
        5 => "控制器互斥被占用",
        6 => "协议事件创建失败",
        7 => "SetWindowsHookEx 失败",
        8 => "游戏在安装期间退出",
        9 => "安装失败",
        10 => "安装等待失败",
        _ => $"未知结果码 {result}",
    };

    // ---- native exports: ZZZTouchCore.dll (__cdecl) ----

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchInjectToProcess(
        uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchWaitGameExit(uint timeoutMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ZZZTouchRelease();

    // ---- user32: 等待主窗口客户区就绪 ----

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
