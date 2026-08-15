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

    public static bool IsAllowed(GameType game, string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return For(game).Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
