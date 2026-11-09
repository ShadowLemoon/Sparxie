using Sparxie.Contracts.Models;

namespace Sparxie.Desktop.Services;

public enum CloseBehavior
{
    /// <summary>退出前弹确认。</summary>
    Ask,

    /// <summary>关闭窗口时最小化到系统托盘，托盘菜单里退出。</summary>
    Tray,

    /// <summary>直接退出。</summary>
    Exit,
}

internal static class DesktopSettingsExtensions
{
    public static CloseBehavior ResolveCloseBehavior(this DesktopSettings settings) =>
        Enum.TryParse<CloseBehavior>(settings.CloseAction, out var parsed) ? parsed : CloseBehavior.Ask;
}
