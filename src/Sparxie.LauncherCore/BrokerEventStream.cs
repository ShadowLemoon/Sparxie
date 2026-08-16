using Grpc.Core;
using Sparxie.Contracts.Rpc;

namespace Sparxie.LauncherCore;

/// <summary>Broker 单一控制事件流的非 UI 包装。</summary>
public sealed class BrokerEventStream : IAsyncDisposable
{
    private AsyncServerStreamingCall<SessionEvent>? _call;

    internal BrokerEventStream(AsyncServerStreamingCall<SessionEvent> call)
    {
        _call = call;
    }

    public IAsyncEnumerable<SessionEvent> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return (_call ?? throw new ObjectDisposedException(nameof(BrokerEventStream)))
            .ResponseStream
            .ReadAllAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _call?.Dispose();
        _call = null;
        return ValueTask.CompletedTask;
    }
}
