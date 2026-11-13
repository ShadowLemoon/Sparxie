using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Sparxie.LauncherCore;

/// <summary>
/// 调用方传入的 Profile 创建/修改字段；null 表示该字段未指定。
/// <see cref="Launch"/> 非 null 时整体替换该配置的启动设置（所有字段都是“不修改”则清除这一段）。
/// </summary>
public sealed record ProfileMutation(
    string? Id = null,
    string? DisplayName = null,
    GameType? Game = null,
    string? ExecutablePath = null,
    bool? FpsUnlockEnabled = null,
    int? TargetFps = null,
    bool? BackgroundFpsLimitEnabled = null,
    int? BackgroundFps = null,
    ProcessPriority? ProcessPriority = null,
    bool? GenshinFollowInGamePreset = null,
    int? GenshinPreset30Fps = null,
    int? GenshinPreset60Fps = null,
    bool? GenshinTouchUiScaleOverrideEnabled = null,
    int? GenshinTouchUiScalePercent = null,
    string? EndfieldInputMode = null,
    bool? EndfieldAutoResolution = null,
    int? EndfieldWidth = null,
    int? EndfieldHeight = null,
    int? EndfieldRefreshRate = null,
    string? EndfieldUiScaleMode = null,
    int? EndfieldUiScalePercent = null,
    string? EndfieldSafeAreaMode = null,
    int? EndfieldSafeLeft = null,
    int? EndfieldSafeRight = null,
    LaunchSettings? Launch = null)
{
    public bool HasHoyoSettings =>
        FpsUnlockEnabled.HasValue
        || TargetFps.HasValue
        || BackgroundFpsLimitEnabled.HasValue
        || BackgroundFps.HasValue
        || ProcessPriority.HasValue;

    public bool HasEndfieldSettings =>
        EndfieldInputMode is not null
        || EndfieldAutoResolution.HasValue
        || EndfieldWidth.HasValue
        || EndfieldHeight.HasValue
        || EndfieldRefreshRate.HasValue
        || EndfieldUiScaleMode is not null
        || EndfieldUiScalePercent.HasValue
        || EndfieldSafeAreaMode is not null
        || EndfieldSafeLeft.HasValue
        || EndfieldSafeRight.HasValue;

    public bool HasGenshinSettings =>
        GenshinFollowInGamePreset.HasValue
        || GenshinPreset30Fps.HasValue
        || GenshinPreset60Fps.HasValue
        || GenshinTouchUiScaleOverrideEnabled.HasValue
        || GenshinTouchUiScalePercent.HasValue;

    public bool HasAnySettableField =>
        !string.IsNullOrWhiteSpace(DisplayName)
        || !string.IsNullOrWhiteSpace(ExecutablePath)
        || Game.HasValue
        || HasHoyoSettings
        || HasGenshinSettings
        || HasEndfieldSettings
        || Launch is not null;
}
