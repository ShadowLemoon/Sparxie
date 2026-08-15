using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Sparxie.Contracts.Rpc;
using Sparxie.SessionHost.Hosting;
using Sparxie.SessionHost.Sessions;
using HostOptions = Sparxie.SessionHost.Hosting.HostOptions;

namespace Sparxie.SessionHost.Services;

/// <summary>
/// Host 私有管道服务：Broker 作为客户端连接。Broker 下发命令流，Host 上报事件流。
/// 会话结束（Exited/Failed）后停止 Host 应用，Host 进程随之退出。
/// </summary>
public sealed class HostService : SparxieHost.SparxieHostBase
{
    private readonly IHostApplicationLifetime _lifetime;

    public HostService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public override async Task Connect(
        IAsyncStreamReader<HostCommand> requestStream,
        IServerStreamWriter<HostEvent> responseStream,
        ServerCallContext context)
    {
        var options = HostOptions.FromEnvironment();
        var events = Channel.CreateUnbounded<HostEvent>();

        using var controller = CreateController(options);
        await using var session = new GameSession(options, controller, ev => events.Writer.WriteAsync(ev).AsTask());
        var reportTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in events.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    await responseStream.WriteAsync(ev).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端断开
            }
            catch (IOException)
            {
                // 管道中断
            }
        });

        var commandTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var command in requestStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    switch (command.Command)
                    {
                        case "set_target_fps" when session.IsRunning:
                            session.SetTargetFps(command.TargetFps);
                            break;
                        case "shutdown":
                            // 预留：停止会话命令（首版 UI 不提供）
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        });

        await session.RunAsync(context.CancellationToken).ConfigureAwait(false);

        events.Writer.TryComplete();
        await reportTask.ConfigureAwait(false);

        // 会话生命周期已结束：立即停止 Host，不等待命令流优雅关闭。
        // commandTask 作为后台任务随进程退出终止。
        _lifetime.StopApplication();
    }

    private static IGameController CreateController(HostOptions options) => options.Profile.Game switch
    {
        "zenlessZoneZero" => new ZzzGameController(options.SessionId),
        // Hoyo（原神/星铁）真实流程尚未接入上游扫描/Patch：
        // 接入后改为 new HoyoGameController()，当前保持 Null 占位避免误报能力。
        _ => new NullGameController(),
    };
}
