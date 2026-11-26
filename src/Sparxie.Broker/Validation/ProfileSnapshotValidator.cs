using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Broker.Validation;

/// <summary>Broker 边界的 Profile 快照重新验证：路径、枚举、数值范围。</summary>
public static class ProfileSnapshotValidator
{
    private static readonly CombinedGameCatalog Builtin = new(new PluginCatalog(string.Empty, new PluginTrustStore(string.Empty)));

    public const int MinFps = 10;
    public const int MaxFps = 1000;
    public const int MinTouchUiScalePercent = 100;
    public const int MaxTouchUiScalePercent = 500;


    private static readonly HashSet<string> ValidPriorities = new(StringComparer.Ordinal)
    {
        "normal",
        "belowNormal",
        "aboveNormal",
        "high",
    };

    /// <summary>返回 null 表示通过；否则为拒绝原因（稳定错误码 + 用户可读消息）。</summary>
    public static (ErrorCode Code, string Message)? Validate(ProfileSnapshot profile, CombinedGameCatalog? catalog = null)
    {
        if (profile is null)
        {
            return (ErrorCode.InvalidArgument, "缺少 profile 快照");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileId))
        {
            return (ErrorCode.InvalidArgument, "profile.profileId 不能为空");
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return (ErrorCode.InvalidArgument, "profile.displayName 不能为空");
        }

        if ((catalog ?? Builtin).Find(profile.Game) is null || profile.Game != GameIds.Normalize(profile.Game))
        {
            return (ErrorCode.InvalidArgument, $"profile.game 非法: {profile.Game}");
        }

        if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
        {
            return (ErrorCode.InvalidArgument, "profile.executablePath 不能为空");
        }

        // EXE 白名单：原神仅 YuanShen.exe/GenshinImpact.exe，星铁仅 StarRail.exe，
        // 绝区零仅 ZenlessZoneZero.exe/ZenlessZoneZeroBeta.exe
        if (!(catalog ?? Builtin).IsAllowed(profile.Game, profile.ExecutablePath))
        {
            return (ErrorCode.InvalidArgument, $"executablePath 不在 {profile.Game} 白名单: {Path.GetFileName(profile.ExecutablePath)}");
        }

        if (profile.Launch is { } launch)
        {
            if (LaunchSettingsRules.Validate(
                    launch.Width == 0 ? null : launch.Width,
                    launch.Height == 0 ? null : launch.Height,
                    launch.ExtraArguments) is { } launchError)
            {
                return (ErrorCode.InvalidArgument, $"launch 非法: {launchError}");
            }

            if (launch.Fullscreen is < 0 or > 2)
            {
                return (ErrorCode.InvalidArgument, $"launch.fullscreen 非法: {launch.Fullscreen}");
            }
        }

        if (!string.IsNullOrEmpty(profile.SettingsJson))
        {
            // 内置游戏没有插件设置；第三方游戏的设置只做结构检查（大小、是 JSON 对象），具体取值由 SessionHost 按 schema 归一化，不当作拒绝理由。
            if (GameIds.TryGetBuiltIn(profile.Game, out _))
            {
                return (ErrorCode.InvalidArgument, "内置游戏不支持 settingsJson");
            }

            if (profile.SettingsJson.Length > PluginSettingsSchema.MaxSettingsBytes
                || PluginSettingsSchema.ParseObject(profile.SettingsJson) is null)
            {
                return (ErrorCode.InvalidArgument, "settingsJson 必须是不超过 16KB 的 JSON 对象");
            }
        }

        if (profile.Hoyo is { } hoyo)
        {
            if (hoyo.TargetFps is < MinFps or > MaxFps)
            {
                return (ErrorCode.InvalidArgument, $"targetFps 超出 {MinFps}–{MaxFps}");
            }

            if (hoyo.BackgroundFps is < MinFps or > MaxFps)
            {
                return (ErrorCode.InvalidArgument, $"backgroundFps 超出 {MinFps}–{MaxFps}");
            }

            if (hoyo.GenshinPreset30Fps is < MinFps or > MaxFps)
            {
                return (ErrorCode.InvalidArgument, $"genshinPreset30Fps 超出 {MinFps}–{MaxFps}");
            }

            if (hoyo.GenshinPreset60Fps is < MinFps or > MaxFps)
            {
                return (ErrorCode.InvalidArgument, $"genshinPreset60Fps 超出 {MinFps}–{MaxFps}");
            }

            if (hoyo.GenshinTouchUiScalePercent is < MinTouchUiScalePercent or > MaxTouchUiScalePercent)
            {
                return (ErrorCode.InvalidArgument, $"genshinTouchUiScalePercent 超出 {MinTouchUiScalePercent}–{MaxTouchUiScalePercent}");
            }

            if (!ValidPriorities.Contains(hoyo.ProcessPriority))
            {
                return (ErrorCode.InvalidArgument, $"processPriority 非法: {hoyo.ProcessPriority}");
            }

            if (profile.DisableTouchUi && !hoyo.FpsUnlockEnabled)
            {
                return (ErrorCode.InvalidArgument, "disableTouchUi 为 true 时 fpsUnlockEnabled 不能为 false");
            }
        }

        return null;
    }
}
