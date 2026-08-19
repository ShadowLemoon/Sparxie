using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;

namespace Sparxie.LauncherCore;

/// <summary>唯一的 Profile → 跨进程快照映射入口。</summary>
public static class ProfileSnapshotMapper
{
    public static ProfileSnapshot Map(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new ProfileSnapshot
        {
            ProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            Game = profile.Game switch
            {
                GameType.Genshin => "genshin",
                GameType.StarRail => "starRail",
                GameType.ZenlessZoneZero => "zenlessZoneZero",
                _ => throw new LauncherException(
                    LauncherFailureKind.ProfileSelection,
                    $"不支持的游戏类型: {profile.Game}"),
            },
            ExecutablePath = profile.ExecutablePath,
            Hoyo = profile.Hoyo is null
                ? null
                : new HoyoSettings
                {
                    FpsUnlockEnabled = profile.Hoyo.FpsUnlockEnabled,
                    TargetFps = profile.Hoyo.TargetFps,
                    BackgroundFpsLimitEnabled = profile.Hoyo.BackgroundFpsLimitEnabled,
                    BackgroundFps = profile.Hoyo.BackgroundFps,
                    ProcessPriority = profile.Hoyo.ProcessPriority switch
                    {
                        ProcessPriority.BelowNormal => "belowNormal",
                        ProcessPriority.AboveNormal => "aboveNormal",
                        ProcessPriority.High => "high",
                        _ => "normal",
                    },
                    GenshinFollowInGamePreset = profile.Hoyo.GenshinFollowInGamePreset,
                    GenshinPreset30Fps = profile.Hoyo.GenshinPreset30Fps,
                    GenshinPreset60Fps = profile.Hoyo.GenshinPreset60Fps,
                    GenshinTouchUiScaleOverrideEnabled = profile.Hoyo.GenshinTouchUiScaleOverrideEnabled,
                    GenshinTouchUiScalePercent = profile.Hoyo.GenshinTouchUiScalePercent,
                },
        };
    }
}
