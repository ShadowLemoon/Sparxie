using System.Diagnostics;
using System.Runtime.InteropServices;
using Sparxie.Contracts.Rpc;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// 绝区零 Runtime 控制器（按 ZZZTouchLauncher v2 重构）：只负责把 ZZZTouchRuntime 注入游戏进程。
/// v2 的 Runtime 在游戏进程内固定触屏布局、合成多指触点，因此<b>不再读写任何游戏配置</b>
/// （不备份/改写 GENERAL_DATA.bin，也没有恢复记录），崩溃或异常退出都没有配置残留。
/// 触屏布局必须在游戏启动、界面建好之前固定，所以只支持“启动时注入”，不支持游戏已在运行时中途注入。
/// </summary>
public sealed class ZzzGameController : IGameController
{
    public bool CreatesProcess => false;

    /// <summary>首次等待游戏主窗口的时长（毫秒），与 ZZZTouchLauncher v2 一致。</summary>
    private const uint FirstWindowWaitMs = 60_000;

    /// <summary>首次返回“未找到主窗口”时的二次尝试等待时长与间隔，与 v2 的 Controller 重试一致。</summary>
    private const uint RetryWindowWaitMs = 30_000;

    private const int RetryDelayMs = 5_000;

    private readonly string _sessionId;
    private bool _injected;
    private bool _released;

    public ZzzGameController(string sessionId)
    {
        _sessionId = sessionId;
    }

    public string SessionId => _sessionId;

    public Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken)
    {
        // 不再有启动前的配置处理；只确认 EXE 存在，早失败比让游戏起不来再报错清楚。
        if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || !File.Exists(profile.ExecutablePath))
        {
            throw new FileNotFoundException("绝区零 EXE 不存在", profile.ExecutablePath);
        }

        return Task.CompletedTask;
    }

    public async Task InstallAsync(
        Process? gameProcess,
        IntPtr jobHandle,
        Sparxie.Contracts.Models.HoyoProfileSettings? hoyo,
        CancellationToken cancellationToken)
    {
        // ZZZ 由 SessionHost 预创建进程（CreatesProcess=false），运行期 gameProcess 非空
        if (gameProcess is null)
        {
            throw new InvalidOperationException("ZZZ 游戏进程未创建");
        }

        var targetPid = (uint)gameProcess.Id;
        var result = ZZZTouchInjectToProcess(targetPid, quiet: true, FirstWindowWaitMs);
        if (result == 1)
        {
            // 仅“未找到主窗口”才重试；其他失败（安装失败/已有注入会话等）重试只会得到误导性的错误码。
            // 游戏有时会重新拉起一个进程，重试前按当前存活的游戏进程重新取 PID。
            await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
            targetPid = FindRunningGamePid() ?? targetPid;
            result = ZZZTouchInjectToProcess(targetPid, quiet: true, RetryWindowWaitMs);
        }

        if (result != 0)
        {
            // 失败时也要释放 native 持有的 Hook/互斥，避免下次启动被“已有注入控制器”挡住。
            ZZZTouchRelease();
            throw new InvalidOperationException($"ZZZ Runtime 注入失败: {DescribeInjectResult(result)}");
        }

        _injected = true;
    }

    public Task PostInstallAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    private static uint? FindRunningGamePid()
    {
        foreach (var name in new[] { "ZenlessZoneZero", "ZenlessZoneZeroBeta" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        return (uint)process.Id;
                    }
                }
                catch (InvalidOperationException)
                {
                    // 进程刚好退出，换下一个。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    /// <summary>ZZZTouchCore v2 的返回码（来自 ZZZTouchLauncher v2 的 DescribeInjectResult）。</summary>
    private static string DescribeInjectResult(int result) => result switch
    {
        0 => "成功",
        1 => "未找到游戏主窗口",
        2 => "加载 ZZZTouchRuntime.dll 失败",
        3 => "获取合成器导出入口失败",
        4 => "打开游戏进程句柄失败（请以管理员权限运行）",
        5 => "已有另一个注入控制器占用（互斥锁）",
        6 => "协议事件创建失败（进程可能已有注入会话，请重启游戏）",
        7 => "安装 WH_GETMESSAGE Hook 失败",
        8 => "合成器安装失败（详见游戏侧行为）",
        9 => "游戏在安装确认前退出",
        10 => "等待 Runtime 安装结果失败",
        _ => $"未知错误({result})",
    };

    // ---- native exports: ZZZTouchCore.dll (__cdecl) ----

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchInjectToProcess(
        uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ZZZTouchRelease();
}
