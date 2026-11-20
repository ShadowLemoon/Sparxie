namespace Sparxie.Contracts.Rpc;

/// <summary>RPC 契约常量：协议版本等跨进程公共约定。</summary>
public static class RpcContract
{
    public const int ProtocolVersion = 1;
    public const string ProductName = "Sparxie";

    /// <summary>Broker 认识 HoyoSettings.touch_ui_enabled：可以启动不带触屏界面的会话；旧 Broker 会忽略该字段并照常启动触屏界面。</summary>
    public const string CapabilityTouchUiSwitch = "touch-ui-switch";

    /// <summary>当前 Broker 支持的可选能力，Ping 响应的 capabilities 与包内 capabilities.json 都来自这一份清单。</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
    [
        CapabilityTouchUiSwitch,
    ];
}
