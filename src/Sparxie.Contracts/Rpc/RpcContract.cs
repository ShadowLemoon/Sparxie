namespace Sparxie.Contracts.Rpc;

/// <summary>RPC 契约常量：协议版本等跨进程公共约定。</summary>
public static class RpcContract
{
    public const int ProtocolVersion = 1;
    public const string ProductName = "Sparxie";

    /// <summary>Broker 认识 ProfileSnapshot.screen：会话的宽高与窗口模式会变成游戏的 Unity 命令行参数。</summary>
    public const string CapabilityScreenArgs = "screen-args";

    /// <summary>当前 Broker 支持的可选能力，Ping 响应的 capabilities 与包内 capabilities.json 都来自这一份清单。</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
    [
        CapabilityScreenArgs,
    ];
}
