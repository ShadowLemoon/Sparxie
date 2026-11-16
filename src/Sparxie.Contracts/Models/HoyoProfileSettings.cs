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

    // Endfield touch interface (written to endfield-ui.ini before launch and while the session runs). Every field is optional in config.json: a missing or
    // unreadable value means the default, and none of them is validated as a reason to drop a profile (see ConfigRepair).
    // Scale: "auto" (the client device decides) or "manual" with EndfieldUiScalePercent (100–180 = 1.00–1.80).
    public string EndfieldUiScaleMode { get; set; } = EndfieldUiSettings.Auto;

    public int EndfieldUiScalePercent { get; set; } = EndfieldUiSettings.DefaultScalePercent;

    // Safe margins for notched screens: "auto" (device table by stream size), "off", or "manual" with the left and right inset in pixels.
    public string EndfieldSafeAreaMode { get; set; } = EndfieldUiSettings.Auto;

    public int EndfieldSafeLeft { get; set; }

    public int EndfieldSafeRight { get; set; }
}
