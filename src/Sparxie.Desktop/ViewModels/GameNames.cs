using Sparxie.Contracts.Models;

namespace Sparxie.Desktop.ViewModels;

public static class GameNames
{
    public static string Display(GameType game) => game switch
    {
        GameType.Genshin => "原神",
        GameType.StarRail => "星穹铁道",
        GameType.ZenlessZoneZero => "绝区零",
        _ => game.ToString(),
    };
}
