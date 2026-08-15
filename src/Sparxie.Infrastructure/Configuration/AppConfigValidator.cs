using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>schema v1 必要字段校验。校验失败视为配置损坏，进入备份恢复流程。</summary>
public static class AppConfigValidator
{
    public const int MinFps = 10;
    public const int MaxFps = 1000;
    public const int MinTouchUiScalePercent = 100;
    public const int MaxTouchUiScalePercent = 500;

    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var errors = new List<string>();

        if (config.SchemaVersion != AppConfig.CurrentSchemaVersion)
        {
            errors.Add($"不支持的 schemaVersion: {config.SchemaVersion}");
            return errors;
        }

        if (config.Profiles is null)
        {
            errors.Add("profiles 不能为 null");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                errors.Add("profile.id 不能为空");
            }
            else if (!ids.Add(profile.Id))
            {
                errors.Add($"重复的 profile.id: {profile.Id}");
            }

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                errors.Add($"profile {profile.Id} 的 displayName 不能为空");
            }

            if (!Enum.IsDefined(profile.Game))
            {
                errors.Add($"profile {profile.Id} 的 game 非法: {profile.Game}");
            }

            if (string.IsNullOrWhiteSpace(profile.Variant))
            {
                errors.Add($"profile {profile.Id} 的 variant 不能为空");
            }

            if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
            {
                // 空路径 = 未完成配置（UI 允许先建 Profile 占位后填路径）；
                // 启动时由 SessionHost 边界拒绝（ExecutableNotFound）。
                // 非空时必须通过游戏类型 EXE 白名单。
            }
            else if (!GameExecutables.IsAllowed(profile.Game, profile.ExecutablePath))
            {
                errors.Add($"profile {profile.Id} 的 EXE 名称不在白名单: {Path.GetFileName(profile.ExecutablePath)}");
            }

            if (profile.Game == GameType.ZenlessZoneZero)
            {
                if (profile.Hoyo is not null)
                {
                    errors.Add($"profile {profile.Id} 的绝区零不应包含 hoyo 设置");
                }
            }
            else if (profile.Hoyo is { } hoyo)
            {
                ValidateFps(errors, profile.Id, nameof(HoyoProfileSettings.TargetFps), hoyo.TargetFps);
                ValidateFps(errors, profile.Id, nameof(HoyoProfileSettings.BackgroundFps), hoyo.BackgroundFps);
                ValidateFps(errors, profile.Id, nameof(HoyoProfileSettings.GenshinPreset30Fps), hoyo.GenshinPreset30Fps);
                ValidateFps(errors, profile.Id, nameof(HoyoProfileSettings.GenshinPreset60Fps), hoyo.GenshinPreset60Fps);

                if (hoyo.GenshinTouchUiScalePercent is < MinTouchUiScalePercent or > MaxTouchUiScalePercent)
                {
                    errors.Add($"profile {profile.Id} 的 genshinTouchUiScalePercent 超出 {MinTouchUiScalePercent}–{MaxTouchUiScalePercent}");
                }

                if (!Enum.IsDefined(hoyo.ProcessPriority))
                {
                    errors.Add($"profile {profile.Id} 的 processPriority 非法");
                }
            }
        }

        return errors;
    }

    private static void ValidateFps(List<string> errors, string profileId, string field, int value)
    {
        if (value is < MinFps or > MaxFps)
        {
            errors.Add($"profile {profileId} 的 {field} 超出 {MinFps}–{MaxFps}");
        }
    }
}
