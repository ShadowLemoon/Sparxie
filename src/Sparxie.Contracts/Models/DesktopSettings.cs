namespace Sparxie.Contracts.Models;

/// <summary>桌面端界面偏好，作为 config.json 的可选 desktop 段保存，不影响启动领域数据。</summary>
public sealed class DesktopSettings
{
    public string Theme { get; set; } = "System";

    /// <summary>点击窗口关闭按钮时：Ask 退出前确认 / Tray 最小化到系统托盘 / Exit 直接退出；缺省按 Ask。</summary>
    public string? CloseAction { get; set; }

    /// <summary>调试模式：主页显示启动/注入状态条等诊断信息。默认关闭；环境变量 SPARXIE_DEBUG=1 可强制开启。</summary>
    public bool DebugMode { get; set; }

    /// <summary>游戏进入运行后主窗口的行为：Keep 保持不变（缺省）/ Minimize 最小化。</summary>
    public string? AfterLaunch { get; set; }

    /// <summary>主页最后选中的游戏标签，跨启动持久化。</summary>
    public string? SelectedGame { get; set; }

    /// <summary>每个游戏上次使用的配置 ID（键为 GameType 名）。点游戏标题时据此恢复，保证结果可预期。</summary>
    public Dictionary<string, string> LastProfileByGame { get; set; } = new();
}
