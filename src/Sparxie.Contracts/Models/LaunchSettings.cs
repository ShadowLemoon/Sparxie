namespace Sparxie.Contracts.Models;

/// <summary>
/// 启动设置（对齐 Starward 的“分辨率”区）：
/// 分辨率与全屏 / 窗口在启动前写入游戏自己的注册表设置；“无边框窗口”是命令行参数 -popupwindow（自动注入），
/// 另可附加自定义命令行参数。Width / Height / Fullscreen 为 null 表示“不修改游戏自己的设置”。
/// </summary>
public sealed class LaunchSettings
{
    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>true 全屏，false 窗口，null 不修改。</summary>
    public bool? Fullscreen { get; set; }

    /// <summary>使用无边框窗口：自动给游戏加 -popupwindow，与是否全屏无关（同 Starward）。</summary>
    public bool Borderless { get; set; }

    /// <summary>附加命令行参数（原样追加在自动注入的参数之后）。</summary>
    public string? ExtraArguments { get; set; }

    /// <summary>所有字段都是“不修改 / 关闭”时视为没有启动设置，配置里不再保存这一段。</summary>
    public bool IsEmpty =>
        Width is null && Height is null && Fullscreen is null && !Borderless && string.IsNullOrWhiteSpace(ExtraArguments);
}
