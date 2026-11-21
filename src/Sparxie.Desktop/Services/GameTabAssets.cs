using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Sparxie.Desktop.Services;

/// <summary>游戏图标本地路径缓存，供标题栏标签模板使用。第三方游戏没有图标，Get 返回 null，标签退化为纯文字。</summary>
public static class GameIconCache
{
    private static readonly Dictionary<GameKey, string> Paths = [];

    public static void Set(GameKey game, string path) => Paths[game] = path;

    public static string? Get(GameKey game) => Paths.TryGetValue(game, out var path) ? path : null;
}

/// <summary>x:Bind 函数绑定辅助：游戏标签的显示名与图标。</summary>
public static class GameTabBinding
{
    public static string Name(GameKey game) => game.Display;

    public static ImageSource? Icon(GameKey game) => GameIconCache.Get(game) is { } path
        ? new BitmapImage(new Uri(path))
        : null;
}
