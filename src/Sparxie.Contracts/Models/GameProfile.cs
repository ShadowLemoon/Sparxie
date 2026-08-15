namespace Sparxie.Contracts.Models;

/// <summary>一个用户可管理的游戏安装 Profile。完整 EXE 路径是安装身份的唯一持久化真相源。</summary>
public sealed class GameProfile
{
    public required string Id { get; set; }

    public required string DisplayName { get; set; }

    public required GameType Game { get; set; }

    /// <summary>地区/正式/Beta 等变体标识，如 "cn"、"intl"、"beta"。不改变同款游戏互斥规则。</summary>
    public required string Variant { get; set; }

    public required string ExecutablePath { get; set; }

    /// <summary>仅原神/星铁适用；绝区零为 null。</summary>
    public HoyoProfileSettings? Hoyo { get; set; }
}
