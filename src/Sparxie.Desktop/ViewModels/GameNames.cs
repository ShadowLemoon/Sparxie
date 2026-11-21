using Sparxie.Desktop.Services;

namespace Sparxie.Desktop.ViewModels;

public static class GameNames
{
    /// <summary>显示名：内置三款写死中文名，第三方游戏取 plugin.json 的 displayName。</summary>
    public static string Display(GameKey game) => game.Display;
}
