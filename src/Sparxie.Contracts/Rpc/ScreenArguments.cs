using System.Globalization;

namespace Sparxie.Contracts.Rpc;

/// <summary>
/// ProfileSnapshot.screen → the Unity player command-line arguments appended to the game process.
/// Only integers and fixed tokens are ever emitted, so a client cannot put its own text on the game's command line.
/// Sources: Unity manual, Standalone Player command-line arguments (-screen-width, -screen-height, -screen-fullscreen 0|1, -popupwindow, -window-mode);
/// the community launchers Collapse (GameSettings/{Genshin,StarRail,Zenless}/Settings.cs, GetLaunchArguments) and Starward
/// (GameLauncherService.cs, -popupwindow) pass the same arguments to these titles. Both are MIT; only the behaviour is used here.
/// </summary>
public static class ScreenArguments
{
    public const int MinSize = 320;
    public const int MaxSize = 16384;

    public const string Fullscreen = "fullscreen";
    public const string Windowed = "windowed";
    public const string Borderless = "borderless";

    public static bool IsEmpty(ScreenArgs? screen) =>
        screen is null || (screen.Width == 0 && screen.Height == 0 && string.IsNullOrEmpty(screen.WindowMode));

    /// <summary>Null when the screen arguments are valid (or absent); otherwise one readable reason.</summary>
    public static string? Validate(ScreenArgs? screen)
    {
        if (screen is null)
        {
            return null;
        }

        if ((screen.Width == 0) != (screen.Height == 0))
        {
            return "screen.width 与 screen.height 须同时给出";
        }

        if (screen.Width != 0 && (screen.Width is < MinSize or > MaxSize || screen.Height is < MinSize or > MaxSize))
        {
            return $"screen 尺寸超出 {MinSize}–{MaxSize}";
        }

        if (!string.IsNullOrEmpty(screen.WindowMode) && screen.WindowMode is not (Fullscreen or Windowed or Borderless))
        {
            return $"screen.windowMode 非法: {screen.WindowMode}";
        }

        return null;
    }

    /// <summary>
    /// The argument text (no leading space), e.g. <c>-screen-width 1280 -screen-height 720 -screen-fullscreen 0</c>; empty when nothing is asked.
    /// An invalid object also gives empty: the Broker has already refused it, and a game must never start with a half-built line.
    /// </summary>
    public static string Build(ScreenArgs? screen)
    {
        if (IsEmpty(screen) || Validate(screen) is not null)
        {
            return string.Empty;
        }

        var parts = new List<string>(6);
        if (screen!.Width != 0)
        {
            parts.Add("-screen-width " + screen.Width.ToString(CultureInfo.InvariantCulture));
            parts.Add("-screen-height " + screen.Height.ToString(CultureInfo.InvariantCulture));
        }

        switch (screen.WindowMode)
        {
            case Fullscreen:
                parts.Add("-screen-fullscreen 1");
                break;
            case Windowed:
                parts.Add("-screen-fullscreen 0");
                break;
            case Borderless:
                // -popupwindow makes the window a frameless dialog window; it only shows while the player is not full screen
                parts.Add("-screen-fullscreen 0");
                parts.Add("-popupwindow");
                break;
        }

        return string.Join(' ', parts);
    }

    /// <summary>A full CreateProcess command line: the quoted executable, then the arguments; empty when no argument is asked.</summary>
    public static string CommandLine(string executablePath, ScreenArgs? screen)
    {
        var arguments = Build(screen);
        return arguments.Length == 0 ? string.Empty : "\"" + executablePath + "\" " + arguments;
    }
}
