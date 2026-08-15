using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Sparxie.Broker.Sessions;

/// <summary>
/// Broker 侧的 Host 会话客户端：连接 Host 私有管道，建立 Connect 双向流，
/// 把 Host 事件交给注册表处理器，并下发 Broker 收到的命令。
/// Host 崩溃（流中断且未正常退出）时按最后状态发布 HostCrashed 事件。
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Action<HostEvent> _onHostEvent;

    private GrpcChannel? _channel;
    private AsyncDuplexStreamingCall<HostCommand, HostEvent>? _call;
    private Task? _receiveTask;
    private volatile string _lastState = string.Empty;
    private volatile bool _gracefulExit;
    private bool _disposed;

    public HostSession(string sessionId, string pipeName, string game, Action<HostEvent> onHostEvent)
    {
        SessionId = sessionId;
        _pipeName = pipeName;
        Game = game;
        _onHostEvent = onHostEvent;
    }

    public string SessionId { get; }

    public string Game { get; }

    public string LastState => _lastState;

    /// <summary>连接 Host 管道并启动双向流；Host 尚未就绪时轮询重试。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                // gRPC 双向流创建是惰性连接：先探测管道可达，失败则重试，
                // 避免流创建成功后因管道未就绪而静默失败。
                using (var probe = await new NamedPipesConnectionFactory(_pipeName).ConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                }

                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_pipeName).ConnectAsync(ct),
                };
                _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
                {
                    HttpHandler = handler,
                });

                var client = new SparxieHost.SparxieHostClient(_channel);
                _call = client.Connect(cancellationToken: cancellationToken);

                _receiveTask = ReceiveLoopAsync(cancellationToken);
                return;
            }
            catch (Exception) when (attempt < 59)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("连接 SessionHost 超时");
    }

    public async Task SendCommandAsync(HostCommand command, CancellationToken cancellationToken)
    {
        if (_call is null)
        {
            throw new InvalidOperationException("Host 会话尚未连接");
        }

        await _call.RequestStream.WriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var ev in _call!.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _lastState = ev.State;
                _onHostEvent(ev);
            }

            _gracefulExit = true;
        }
        catch (Exception)
        {
            // Host 崩溃或管道中断：按最后状态判定 HostCrashed
        }
        finally
        {
            try
            {
                await _call!.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // 管道已断开
            }

            if (!_gracefulExit && _lastState is not ("Exited" or "Failed"))
            {
                _onHostEvent(new HostEvent
                {
                    SessionId = SessionId,
                    State = _lastState == "Running" ? "HostCrashedAfterRunning" : "HostCrashedBeforeRunning",
                    Stage = 0,
                    ErrorCode = 0,
                    Message = "SessionHost 异常退出",
                });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_call is not null)
        {
            try
            {
                await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // 流可能已关闭
            }
        }

        // 不等待 _receiveTask：它可能在当前调用链上（事件处理器内触发 Dispose），
        // 等待自身会造成死锁；流断开后 ReceiveLoop 自行结束。
        _call?.Dispose();
        if (_channel is not null)
        {
            await _channel.ShutdownAsync().ConfigureAwait(false);
            _channel.Dispose();
        }
    }
}
