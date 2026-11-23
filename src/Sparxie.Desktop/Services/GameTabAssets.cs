using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Sparxie.Desktop.Services;

/// <summary>游戏图标本地路径缓存，供标题栏标签模板使用。来源：官方接口（内置游戏）→ 插件声明的 icon → 游戏 EXE 自带图标；都没有时 Get 返回 null，界面显示显示名首字母的占位。</summary>
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

/// <summary>图标占位的纯逻辑（可单元测试）：有没有图标、占位字。</summary>
public static class GameTabAssetsInfo
{
    public static bool HasIcon(GameKey game) => GameIconCache.Get(game) is not null;

    public static string Initial(string? title)
    {
        var text = title?.Trim() ?? string.Empty;
        return text.Length == 0 ? "?" : char.ToUpperInvariant(text[0]).ToString();
    }
}
