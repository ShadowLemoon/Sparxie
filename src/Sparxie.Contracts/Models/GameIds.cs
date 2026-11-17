namespace Sparxie.Contracts.Models;

/// <summary>
/// 游戏 id 常量与规范化。id 与 config.json / proto 中的 camelCase 字符串一致（现有三款内置游戏）；
/// Desktop 偏好里历史上存的是 <see cref="GameType"/> 的 PascalCase 名，读取时用 <see cref="Normalize"/> 兼容。
/// </summary>
public static class GameIds
{
    public const string Genshin = "genshin";
    public const string StarRail = "starRail";
    public const string ZenlessZoneZero = "zenlessZoneZero";

    public static IReadOnlyList<string> BuiltIn { get; } = [Genshin, StarRail, ZenlessZoneZero];

    public static string ToId(GameType game) => game switch
    {
        GameType.Genshin => Genshin,
        GameType.StarRail => StarRail,
        GameType.ZenlessZoneZero => ZenlessZoneZero,
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, null),
    };

    /// <summary>忽略大小写识别内置游戏（兼容 PascalCase 的历史偏好值）；非内置返回 false。</summary>
    public static bool TryGetBuiltIn(string? id, out GameType game)
    {
        foreach (var candidate in Enum.GetValues<GameType>())
        {
            if (string.Equals(id, ToId(candidate), StringComparison.OrdinalIgnoreCase))
            {
                game = candidate;
                return true;
            }
        }

        game = default;
        return false;
    }

    /// <summary>内置游戏规范化为标准 id；其他非空值原样保留（去首尾空白）；空返回 null。</summary>
    public static string? Normalize(string? id)
    {
        if (TryGetBuiltIn(id, out var game))
        {
            return ToId(game);
        }

        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }
}
