using System.Text.Json.Serialization;

namespace Sparxie.Contracts.Models;

/// <summary>一个用户可管理的游戏安装 Profile。完整 EXE 路径是安装身份的唯一持久化真相源。</summary>
public sealed class GameProfile
{
    private string _game = string.Empty;

    public required string Id { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>游戏 id（见 <see cref="GameIds"/>）。内置游戏写入时规范化为 camelCase，config.json 内容与枚举时代一致。</summary>
    public required string Game
    {
        get => _game;
        set => _game = GameIds.Normalize(value) ?? string.Empty;
    }

    /// <summary>内置游戏的枚举形式；第三方 / 未知 id 为 null。仅用于内置游戏的专用逻辑。</summary>
    [JsonIgnore]
    public GameType? BuiltInGame => GameIds.TryGetBuiltIn(_game, out var game) ? game : null;

    public required string ExecutablePath { get; set; }

    /// <summary>仅原神/星铁适用；绝区零为 null。</summary>
    public HoyoProfileSettings? Hoyo { get; set; }

    /// <summary>分辨率 / 窗口模式 / 附加命令行参数；没有任何设置时为 null（不写入配置）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaunchSettings? Launch { get; set; }

    /// <summary>第三方插件游戏的设置值（键由插件 settingsSchema 定义）；内置游戏为 null。读取时不做校验，启动前由 schema 归一化。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Settings { get; set; }
}
