using System.Diagnostics;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// 游戏 Runtime 控制器抽象。ZZZ 控制器负责启动前配置切换与恢复记录，
/// Hoyo 控制器负责扫描与 Patch。所有步骤全部成功才进入 Running。
/// </summary>
public interface IGameController : IDisposable
{
    /// <summary>true 表示 Runtime 自行创建游戏进程（Hoyo bootstrap 内部 CreateProcess）；false 由 SessionHost 预创建。</summary>
    bool CreatesProcess { get; }

    /// <summary>启动游戏进程前调用：ZZZ 备份 PC 配置、写入触屏配置并建立恢复记录；Hoyo 无操作。</summary>
    Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken);

    /// <summary>
    /// 注入并安装 Runtime，全部成功后才返回；失败抛异常。返回后可调用 SetTargetFps。
    /// jobHandle：Running 前失效保护 Job（仅 CreatesProcess=true 时非零；bootstrap
    /// 创建游戏进程后 Assign，Running 前由 GameSession 撤销 kill-on-close）。
    /// </summary>
    Task InstallAsync(Process? gameProcess, IntPtr jobHandle, HoyoProfileSettings? hoyo, CancellationToken cancellationToken);

    /// <summary>注入成功后、宣布 Running 前调用：ZZZ 按已验证时序恢复 PC 文件配置并删除恢复记录；Hoyo 无操作。</summary>
    Task PostInstallAsync(CancellationToken cancellationToken);

    /// <summary>失败路径清理：恢复 PC 配置并清理恢复资产（游戏进程已被终止后调用）。</summary>
    Task AbortAsync(CancellationToken cancellationToken);

    /// <summary>等待 Runtime 创建的进程退出（仅 CreatesProcess=true 时由 GameSession 调用）。</summary>
    Task WaitExitAsync(CancellationToken cancellationToken);

    /// <summary>热调主目标 FPS；纯触屏或未启用 FPS 时抛 NotSupportedException。</summary>
    Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken);
}
