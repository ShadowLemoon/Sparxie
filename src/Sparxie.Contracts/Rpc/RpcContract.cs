namespace Sparxie.Contracts.Rpc;

/// <summary>RPC 契约常量：协议版本等跨进程公共约定。</summary>
public static class RpcContract
{
    public const int ProtocolVersion = 1;
    public const string ProductName = "Sparxie";

    /// <summary>Broker 认识 game = "endfield" 会话（终末地 Runtime 会话控制）。</summary>
    public const string CapabilityEndfield = "endfield";

    /// <summary>当前 Broker 支持的可选能力，Ping 响应的 capabilities 与包内 capabilities.json 都来自这一份清单。</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
    [
        CapabilityEndfield,
    ];
}
