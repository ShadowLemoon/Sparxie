namespace Broker.Tests;

/// <summary>
/// 会启动真实 Sparxie.SessionHost / Sparxie.Broker 进程的集成测试共享同一集合，
/// 串行执行，避免进程名检测（GetProcessesByName）与命名管道相互干扰。
/// </summary>
[CollectionDefinition("SessionHostProcess")]
public sealed class SessionHostProcessCollection
{
}
