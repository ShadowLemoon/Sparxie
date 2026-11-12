namespace Sparxie.Contracts.Models;

/// <summary>原神/星铁 Profile 的 Hoyo 专属设置。FPS 默认开启 120。</summary>
public sealed class HoyoProfileSettings
{
    /// <summary>
    /// 是否安装触屏载荷（原神 Mobile UI 钩子 / 星铁 Bootui）。默认 true，即既有行为；
    /// false 为 PC 界面会话：只做 FPS 解锁等其余功能，游戏保持键鼠 UI。缺省字段按 true 处理。
    /// </summary>
    public bool TouchUiEnabled { get; set; } = true;

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
