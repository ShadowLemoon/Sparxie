using System.Threading.Channels;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;

namespace Sparxie.LauncherCore;

/// <summary>
/// 一个 Launcher 进程的控制端：先建立唯一事件流，再启动会话，避免错过早期状态。
/// </summary>
public sealed class LauncherClient : IAsyncDisposable
{
    private readonly BrokerClient _broker;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly object _sessionsGate = new();
    private readonly Dictionary<string, LauncherSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SessionEvent>> _pendingEvents = new(StringComparer.Ordinal);
    private BrokerEventStream? _eventStream;
    private CancellationTokenSource? _controlCancellation;
    private Task? _controlTask;
    private bool _disposed;

    public LauncherClient(BrokerClient broker)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public async Task<LauncherSession> StartSessionAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);

        await EnsureControlStreamAsync(cancellationToken).ConfigureAwait(false);
        var response = await _broker.StartSessionAsync(
            ProfileSnapshotMapper.Map(profile),
            cancellationToken).ConfigureAwait(false);
        if (!response.Accepted || string.IsNullOrWhiteSpace(response.SessionId))
        {
            throw new LauncherSessionRejectedException(
                string.IsNullOrWhiteSpace(response.Message)
                    ? "Broker 拒绝启动会话"
                    : response.Message);
        }

        var session = new LauncherSession(this, response.SessionId, profile);
        lock (_sessionsGate)
        {
            _sessions[session.SessionId] = session;
            if (_pendingEvents.Remove(session.SessionId, out var pending))
            {
                foreach (var ev in pending)
                {
                    session.Publish(ev);
                }

                if (session.IsTerminal)
                {
                    _sessions.Remove(session.SessionId);
                }
            }
        }

        return session;
    }

    internal Task<SetTargetFpsResponse> SetTargetFpsAsync(
        string sessionId,
        int targetFps,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _broker.SetTargetFpsAsync(sessionId, targetFps, cancellationToken);
    }

    private async Task EnsureControlStreamAsync(CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_eventStream is not null)
            {
                return;
            }

            _controlCancellation = new CancellationTokenSource();
            try
            {
                _eventStream = await _broker.OpenEventStreamAsync(
                    _controlCancellation.Token).ConfigureAwait(false);
                var stream = _eventStream;
                var streamCancellation = _controlCancellation;
                _controlTask = Task.Run(
                    () => RouteEventsAsync(stream, streamCancellation.Token),
                    CancellationToken.None);
            }
            catch
            {
                _controlCancellation.Dispose();
                _controlCancellation = null;
                throw;
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task RouteEventsAsync(BrokerEventStream stream, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var ev in stream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                PublishEvent(ev);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                failure = new LauncherException(
                    LauncherFailureKind.SessionFault,
                    "Broker 事件流意外结束");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex is LauncherException
                ? ex
                : new LauncherException(
                    LauncherFailureKind.SessionFault,
                    $"Broker 事件流中断: {ex.Message}",
                    ex);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (failure is not null)
            {
                CompleteActiveSessions(failure);
            }
        }
    }

    private void PublishEvent(SessionEvent ev)
    {
        lock (_sessionsGate)
        {
            if (_sessions.TryGetValue(ev.SessionId, out var session))
            {
                session.Publish(ev);
                if (session.IsTerminal)
                {
                    _sessions.Remove(ev.SessionId);
                }

                return;
            }

            if (!_pendingEvents.TryGetValue(ev.SessionId, out var pending))
            {
                pending = [];
                _pendingEvents[ev.SessionId] = pending;
            }

            pending.Add(ev);
        }
    }

    private void CompleteActiveSessions(Exception failure)
    {
        LauncherSession[] sessions;
        lock (_sessionsGate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _pendingEvents.Clear();
        }

        foreach (var session in sessions)
        {
            session.Complete(failure);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controlCancellation?.Cancel();
        if (_eventStream is not null)
        {
            await _eventStream.DisposeAsync().ConfigureAwait(false);
        }

        if (_controlTask is not null)
        {
            try
            {
                await _controlTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception) when (_controlTask.IsCanceled || _controlTask.IsFaulted)
            {
                // 控制流收尾不应阻塞宿主退出。
            }
            catch (TimeoutException)
            {
                // 管道实现未及时响应取消时，已主动释放事件流，继续关闭本地资源。
            }
        }

        CompleteActiveSessions(new OperationCanceledException("Launcher 控制端已关闭"));
        _controlCancellation?.Dispose();
        _controlCancellation = null;
        _eventStream = null;
        _controlGate.Dispose();
        await _broker.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>单个游戏会话的事件和允许的运行中热调接口。</summary>
public sealed class LauncherSession
{
    private readonly LauncherClient _owner;
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>();
    private bool _terminal;

    internal LauncherSession(LauncherClient owner, string sessionId, GameProfile profile)
    {
        _owner = owner;
        SessionId = sessionId;
        Profile = profile;
    }

    public string SessionId { get; }

    public GameProfile Profile { get; }

    public bool IsTerminal => _terminal;

    /// <summary>单消费者事件流；收到终态后自动结束。</summary>
    public IAsyncEnumerable<SessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task<SetTargetFpsResponse> SetTargetFpsAsync(
        int targetFps,
        CancellationToken cancellationToken = default)
    {
        return _owner.SetTargetFpsAsync(SessionId, targetFps, cancellationToken);
    }

    internal void Publish(SessionEvent ev)
    {
        _events.Writer.TryWrite(ev);
        if (ev.State is "Exited" or "Failed" or "HostCrashedBeforeRunning" or "HostCrashedAfterRunning")
        {
            _terminal = true;
            _events.Writer.TryComplete();
        }
    }

    internal void Complete(Exception failure)
    {
        _events.Writer.TryComplete(failure);
    }
}
