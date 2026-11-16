using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>Puts a value the validator rejects back to its default, so one wrong field costs nothing else. Only what the validator would reject is touched.</summary>
public static class ConfigRepair
{
    public const int DefaultEndfieldWidth = 1920;
    public const int DefaultEndfieldHeight = 1080;
    public const int DefaultEndfieldRefreshRate = 60;

    /// <summary>True when something changed; each change is described in <paramref name="notes"/>.</summary>
    public static bool RepairProfile(GameProfile profile, List<string> notes)
    {
        var before = notes.Count;
        var label = string.IsNullOrWhiteSpace(profile.Id) ? "(无 id)" : $"“{profile.Id}”";

        if (profile.Game == GameType.ZenlessZoneZero)
        {
            if (profile.Hoyo is not null)
            {
                profile.Hoyo = null;
                notes.Add($"profile {label}：绝区零不应有 hoyo 设置，已移除");
            }

            return notes.Count > before;
        }

        if (profile.Hoyo is not { } hoyo)
        {
            return false;
        }

        var defaults = new HoyoProfileSettings();
        Fps(nameof(HoyoProfileSettings.TargetFps), hoyo.TargetFps, defaults.TargetFps, v => hoyo.TargetFps = v);
        Fps(nameof(HoyoProfileSettings.BackgroundFps), hoyo.BackgroundFps, defaults.BackgroundFps, v => hoyo.BackgroundFps = v);
        Fps(nameof(HoyoProfileSettings.GenshinPreset30Fps), hoyo.GenshinPreset30Fps, defaults.GenshinPreset30Fps, v => hoyo.GenshinPreset30Fps = v);
        Fps(nameof(HoyoProfileSettings.GenshinPreset60Fps), hoyo.GenshinPreset60Fps, defaults.GenshinPreset60Fps, v => hoyo.GenshinPreset60Fps = v);

        if (hoyo.GenshinTouchUiScalePercent is < AppConfigValidator.MinTouchUiScalePercent or > AppConfigValidator.MaxTouchUiScalePercent)
        {
            notes.Add($"profile {label}：genshinTouchUiScalePercent {hoyo.GenshinTouchUiScalePercent} 超出范围，已改为 {defaults.GenshinTouchUiScalePercent}");
            hoyo.GenshinTouchUiScalePercent = defaults.GenshinTouchUiScalePercent;
        }

        if (!Enum.IsDefined(hoyo.ProcessPriority))
        {
            notes.Add($"profile {label}：processPriority 非法，已改为 normal");
            hoyo.ProcessPriority = ProcessPriority.Normal;
        }

        if (profile.Game == GameType.Endfield)
        {
            if (hoyo.EndfieldInputMode is not ("touch" or "pc"))
            {
                notes.Add($"profile {label}：endfieldInputMode “{hoyo.EndfieldInputMode}” 非法，已改为 touch");
                hoyo.EndfieldInputMode = "touch";
            }

            if (!hoyo.EndfieldAutoResolution && !AppConfigValidator.IsEndfieldSizeValid(hoyo.EndfieldWidth, hoyo.EndfieldHeight))
            {
                notes.Add($"profile {label}：固定分辨率 {hoyo.EndfieldWidth}×{hoyo.EndfieldHeight} 不可用，改为跟随串流（尺寸 {DefaultEndfieldWidth}×{DefaultEndfieldHeight}）");
                hoyo.EndfieldAutoResolution = true;
                hoyo.EndfieldWidth = DefaultEndfieldWidth;
                hoyo.EndfieldHeight = DefaultEndfieldHeight;
            }

            if (hoyo.EndfieldRefreshRate is < AppConfigValidator.MinEndfieldRefreshRate or > AppConfigValidator.MaxEndfieldRefreshRate)
            {
                notes.Add($"profile {label}：endfieldRefreshRate {hoyo.EndfieldRefreshRate} 超出范围，已改为 {DefaultEndfieldRefreshRate}");
                hoyo.EndfieldRefreshRate = DefaultEndfieldRefreshRate;
            }
        }

        return notes.Count > before;

        void Fps(string field, int value, int fallback, Action<int> set)
        {
            if (value is >= AppConfigValidator.MinFps and <= AppConfigValidator.MaxFps)
            {
                return;
            }

            notes.Add($"profile {label}：{field} {value} 超出 {AppConfigValidator.MinFps}–{AppConfigValidator.MaxFps}，已改为 {fallback}");
            set(fallback);
        }
    }
}
