namespace Sparxie.Broker.Hosting;

/// <summary>生产私有 Broker 的控制流与空闲退出策略。</summary>
public sealed class BrokerLifecycleOptions
{
    public bool Enabled { get; init; }

    public TimeSpan IdleBeforeControl { get; init; } = TimeSpan.FromSeconds(10);
}
