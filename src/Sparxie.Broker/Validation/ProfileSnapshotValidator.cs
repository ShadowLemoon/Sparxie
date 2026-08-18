using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Broker.Validation;

/// <summary>Broker 边界的 Profile 快照重新验证：路径、枚举、数值范围。</summary>
public static class ProfileSnapshotValidator
{
    public const int MinFps = 10;
    public const int MaxFps = 1000;
    public const int MinTouchUiScalePercent = 100;
    public const int MaxTouchUiScalePercent = 500;

    private static readonly HashSet<string> ValidGames = new(StringComparer.Ordinal)
    {
        "genshin",
        "starRail",
        "zenlessZoneZero",
    };

    private static readonly HashSet<string> ValidPriorities = new(StringComparer.Ordinal)
    {
        "normal",
        "belowNormal",
        "aboveNormal",
        "high",
    };

    /// <summary>返回 null 表示通过；否则为拒绝原因（稳定错误码 + 用户可读消息）。</summary>
    public static (ErrorCode Code, string Message)? Validate(ProfileSnapshot profile)
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

        if (!ValidGames.Contains(profile.Game))
        {
            return (ErrorCode.InvalidArgument, $"profile.game 非法: {profile.Game}");
        }

        if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
        {
            return (ErrorCode.InvalidArgument, "profile.executablePath 不能为空");
        }

        // EXE 白名单：原神仅 YuanShen.exe/GenshinImpact.exe，星铁仅 StarRail.exe，
        // 绝区零仅 ZenlessZoneZero.exe/ZenlessZoneZeroBeta.exe
        if (!GameExecutables.IsAllowed(profile.Game switch
            {
                "genshin" => GameType.Genshin,
                "starRail" => GameType.StarRail,
                _ => GameType.ZenlessZoneZero,
            }, profile.ExecutablePath))
        {
            return (ErrorCode.InvalidArgument, $"executablePath 不在 {profile.Game} 白名单: {Path.GetFileName(profile.ExecutablePath)}");
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
        }

        return null;
    }
}
