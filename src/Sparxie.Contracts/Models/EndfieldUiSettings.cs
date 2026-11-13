using System.Globalization;

namespace Sparxie.Contracts.Models;

/// <summary>
/// The Endfield touch-interface settings of a profile and their text form in endfield-ui.ini (<c>ui_scale</c>, <c>safe_area</c>).
/// Everything here is total: any stored or received value maps to something safe, nothing throws, nothing is a reason to reject a profile.
/// </summary>
public static class EndfieldUiSettings
{
    public const string Auto = "auto";
    public const string Manual = "manual";
    public const string Off = "off";

    public const int MinScalePercent = 100;
    public const int MaxScalePercent = 180;
    public const int ScaleStepPercent = 5;

    /// <summary>1.40: the settings card's default and reset value; automatic unknown-device fallback remains the game's phone size (1.25).</summary>
    public const int DefaultScalePercent = 140;

    public const int MaxSafeInset = 600;

    /// <summary>Key names in endfield-ui.ini.</summary>
    public const string ScaleKey = "ui_scale";

    public const string SafeAreaKey = "safe_area";

    public static string NormalizeScaleMode(string? mode) =>
        string.Equals(mode, Manual, StringComparison.OrdinalIgnoreCase) ? Manual : Auto;

    public static string NormalizeSafeAreaMode(string? mode) =>
        string.Equals(mode, Manual, StringComparison.OrdinalIgnoreCase) ? Manual
        : string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase) ? Off
        : Auto;

    /// <summary>Clamps to 100–180 in steps of 5; 0 (a field nobody sent) is the default.</summary>
    public static int NormalizeScalePercent(int percent)
    {
        if (percent == 0)
        {
            return DefaultScalePercent;
        }

        var clamped = Math.Clamp(percent, MinScalePercent, MaxScalePercent);
        var stepped = (int)Math.Round(clamped / (double)ScaleStepPercent, MidpointRounding.AwayFromZero) * ScaleStepPercent;
        return Math.Clamp(stepped, MinScalePercent, MaxScalePercent);
    }

    public static int NormalizeInset(int pixels) => Math.Clamp(pixels, 0, MaxSafeInset);

    /// <summary><c>auto</c> or a number with two decimals (<c>1.25</c>), always with a dot.</summary>
    public static string ScaleValue(string? mode, int percent) =>
        NormalizeScaleMode(mode) == Manual
            ? (NormalizeScalePercent(percent) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)
            : Auto;

    /// <summary><c>auto</c>, <c>off</c> or <c>left,right</c> in pixels.</summary>
    public static string SafeAreaValue(string? mode, int left, int right) =>
        NormalizeSafeAreaMode(mode) switch
        {
            Manual => string.Create(CultureInfo.InvariantCulture, $"{NormalizeInset(left)},{NormalizeInset(right)}"),
            Off => Off,
            _ => Auto,
        };

    /// <summary>The two ini values of a profile; a profile without Endfield settings gets the defaults (both automatic).</summary>
    public static (string UiScale, string SafeArea) IniValues(HoyoProfileSettings? settings) =>
        settings is null
            ? (Auto, Auto)
            : (ScaleValue(settings.EndfieldUiScaleMode, settings.EndfieldUiScalePercent),
               SafeAreaValue(settings.EndfieldSafeAreaMode, settings.EndfieldSafeLeft, settings.EndfieldSafeRight));
}
