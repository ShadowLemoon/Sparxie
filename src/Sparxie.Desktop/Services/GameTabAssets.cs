using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Sparxie.Contracts.Models;

namespace Sparxie.Desktop.Services;

/// <summary>游戏图标本地路径缓存，供标题栏标签模板使用。</summary>
public static class GameIconCache
{
    private static readonly Dictionary<GameType, string> Paths = [];

    public static void Set(GameType game, string path) => Paths[game] = path;

    public static string? Get(GameType game) => Paths.TryGetValue(game, out var path) ? path : null;
}

/// <summary>x:Bind 函数绑定辅助：游戏标签的显示名与图标。</summary>
public static class GameTabBinding
{
    public static string Name(GameType game) => game switch
    {
        GameType.Genshin => "原神",
        GameType.StarRail => "崩坏：星穹铁道",
        GameType.ZenlessZoneZero => "绝区零",
        GameType.Endfield => "明日方舟：终末地",
        _ => game.ToString(),
    };

    public static ImageSource? Icon(GameType game) => GameIconCache.Get(game) is { } path
        ? new BitmapImage(new Uri(path))
        : null;
}
