namespace Sparxie.Contracts.Models;

/// <summary>原神/星铁 Profile 的 Hoyo 专属设置。FPS 默认开启 120。</summary>
public sealed class HoyoProfileSettings
{
    public bool FpsUnlockEnabled { get; set; } = true;

    public int TargetFps { get; set; } = 120;

    public bool BackgroundFpsLimitEnabled { get; set; } = true;

    public int BackgroundFps { get; set; } = 10;

    public ProcessPriority ProcessPriority { get; set; } = ProcessPriority.Normal;

    // 原神专属
    public bool GenshinFollowInGamePreset { get; set; }

    public int GenshinPreset30Fps { get; set; } = 60;

    public int GenshinPreset60Fps { get; set; } = 1000;

    public bool GenshinTouchUiScaleOverrideEnabled { get; set; }

    public int GenshinTouchUiScalePercent { get; set; } = 400;
}
