using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>把校验器会拒绝的值改回默认值，让一个错字段不至于赔上整个 Profile。只动校验器会拒绝的内容。</summary>
public static class ConfigRepair
{
    public static void RepairProfile(GameProfile profile, List<string> notes)
    {
        var label = $"配置“{profile.Id}”";

        if (profile.BuiltInGame is not null
            && !string.IsNullOrWhiteSpace(profile.ExecutablePath)
            && !GameExecutables.IsAllowed(profile.Game, profile.ExecutablePath))
        {
            notes.Add($"{label} 的游戏路径不属于这款游戏（{profile.ExecutablePath}），已清空，需要重新选择");
            profile.ExecutablePath = string.Empty;
        }

        if (profile.Launch is { } launch)
        {
            if (LaunchSettingsRules.Validate(launch.Width, launch.Height, null) is { } sizeError)
            {
                notes.Add($"{label}：{sizeError}，已改为不修改分辨率");
                launch.Width = launch.Height = null;
            }

            if (LaunchSettingsRules.Validate(null, null, launch.ExtraArguments) is { } argumentsError)
            {
                notes.Add($"{label}：{argumentsError}，附加启动参数已清空");
                launch.ExtraArguments = null;
            }
        }

        // 插件游戏的 hoyo 段不识别、原样保留
        if (profile.BuiltInGame is null)
        {
            return;
        }

        if (profile.BuiltInGame == GameType.ZenlessZoneZero)
        {
            if (profile.Hoyo is not null)
            {
                profile.Hoyo = null;
                notes.Add($"{label}：绝区零不应有 hoyo 设置，已移除");
            }

            return;
        }

        if (profile.Hoyo is not { } hoyo)
        {
            return;
        }

        var defaults = new HoyoProfileSettings();
        hoyo.TargetFps = Fps(nameof(hoyo.TargetFps), hoyo.TargetFps, defaults.TargetFps);
        hoyo.BackgroundFps = Fps(nameof(hoyo.BackgroundFps), hoyo.BackgroundFps, defaults.BackgroundFps);
        hoyo.GenshinPreset30Fps = Fps(nameof(hoyo.GenshinPreset30Fps), hoyo.GenshinPreset30Fps, defaults.GenshinPreset30Fps);
        hoyo.GenshinPreset60Fps = Fps(nameof(hoyo.GenshinPreset60Fps), hoyo.GenshinPreset60Fps, defaults.GenshinPreset60Fps);

        if (hoyo.GenshinTouchUiScalePercent is < AppConfigValidator.MinTouchUiScalePercent or > AppConfigValidator.MaxTouchUiScalePercent)
        {
            notes.Add($"{label}：genshinTouchUiScalePercent {hoyo.GenshinTouchUiScalePercent} 超出范围，已改为 {defaults.GenshinTouchUiScalePercent}");
            hoyo.GenshinTouchUiScalePercent = defaults.GenshinTouchUiScalePercent;
        }

        if (!Enum.IsDefined(hoyo.ProcessPriority))
        {
            notes.Add($"{label}：processPriority 非法，已改为 normal");
            hoyo.ProcessPriority = ProcessPriority.Normal;
        }

        int Fps(string field, int value, int fallback)
        {
            if (value is >= AppConfigValidator.MinFps and <= AppConfigValidator.MaxFps)
            {
                return value;
            }

            notes.Add($"{label}：{field} {value} 超出 {AppConfigValidator.MinFps}–{AppConfigValidator.MaxFps}，已改为 {fallback}");
            return fallback;
        }
    }
}
