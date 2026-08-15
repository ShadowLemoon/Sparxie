using System.Diagnostics;
using Sparxie.Contracts.Models;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// 游戏 Runtime 控制器抽象：进入 Running 前的注入/安装/校验步骤。
/// 第四步以 Null 占位；ZZZ 与 Hoyo 控制器在后续步骤接入。
/// </summary>
public interface IGameController : IDisposable
{
    /// <summary>注入并安装 Runtime，全部成功后才返回；失败抛异常。返回后可调用 SetTargetFps。</summary>
    Task InstallAsync(Process gameProcess, HoyoProfileSettings? hoyo, CancellationToken cancellationToken);

    /// <summary>热调主目标 FPS；纯触屏或未启用 FPS 时抛 NotSupportedException。</summary>
    Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken);
}

/// <summary>占位控制器：立即成功，用于 Running 前失效保护与生命周期验证。</summary>
public sealed class NullGameController : IGameController
{
    public Task InstallAsync(Process gameProcess, HoyoProfileSettings? hoyo, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public void Dispose()
    {
    }
}
