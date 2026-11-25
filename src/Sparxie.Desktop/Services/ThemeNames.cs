namespace Sparxie.Desktop.Services;

/// <summary>desktop.theme 的取值与设置下拉框序号的对应：0 跟随系统，1 浅色，2 深色。认不出的值按跟随系统。</summary>
public static class ThemeNames
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static int IndexOf(string? value) =>
        string.Equals(value, Light, global::System.StringComparison.OrdinalIgnoreCase) ? 1
        : string.Equals(value, Dark, global::System.StringComparison.OrdinalIgnoreCase) ? 2
        : 0;

    public static string FromIndex(int index) => index switch { 1 => Light, 2 => Dark, _ => System };
}
