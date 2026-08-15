using System.Collections.Concurrent;
using System.Threading.Channels;
using Sparxie.Contracts.Rpc;

namespace Sparxie.Broker.Sessions;

/// <summary>
/// 会话注册表：Broker 维护的活动会话集合与 UI 事件广播。
/// 游戏级互斥的权威所有者是 SessionHost，注册表只用于先发拒绝与转发。
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new(StringComparer.Ordinal);
    private readonly Channel<SessionEvent> _eventChannel = Channel.CreateUnbounded<SessionEvent>();

    public ChannelReader<SessionEvent> Events => _eventChannel.Reader;

    public void Publish(SessionEvent ev) => _eventChannel.Writer.TryWrite(ev);

    public IReadOnlyCollection<HostSession> Sessions => _sessions.Values.ToArray();

    public bool TryAdd(HostSession session) => _sessions.TryAdd(session.SessionId, session);

    public bool TryGet(string sessionId, out HostSession session) => _sessions.TryGetValue(sessionId, out session!);

    public HostSession? Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out var session);
        return session;
    }

    public bool HasActiveSessionForGame(string game) =>
        _sessions.Values.Any(s => string.Equals(s.Game, game, StringComparison.Ordinal));
}
