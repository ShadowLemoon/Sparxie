using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>各游戏允许的 EXE 白名单。未知 EXE 名称拒绝，避免扩大为任意进程注入器。</summary>
public static class GameExecutables
{
    public static IReadOnlyList<string> For(GameType game) => game switch
    {
        GameType.Genshin => ["YuanShen.exe", "GenshinImpact.exe"],
        GameType.StarRail => ["StarRail.exe"],
        GameType.ZenlessZoneZero => ["ZenlessZoneZero.exe", "ZenlessZoneZeroBeta.exe"],
        _ => [],
    };

    /// <summary>按游戏 id 查白名单；未知 id 返回空（一律拒绝）。</summary>
    public static IReadOnlyList<string> For(string? gameId) =>
        GameIds.TryGetBuiltIn(gameId, out var game) ? For(game) : [];

    public static bool IsAllowed(string? gameId, string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return For(gameId).Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAllowed(GameType game, string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return For(game).Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
