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

    // Endfield 专属启动设置；其他游戏忽略这些字段。
    public string EndfieldInputMode { get; set; } = "touch";

    public bool EndfieldAutoResolution { get; set; } = true;

    public int EndfieldWidth { get; set; } = 1920;

    public int EndfieldHeight { get; set; } = 1080;

    public int EndfieldRefreshRate { get; set; } = 60;
}
