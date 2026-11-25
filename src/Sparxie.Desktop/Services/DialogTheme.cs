using Microsoft.UI.Xaml;

namespace Sparxie.Desktop.Services;

/// <summary>ContentDialog 弹在独立的浮层里，不会继承窗口根元素上设置的主题，需要显式带过去。</summary>
public static class DialogTheme
{
    public static ElementTheme For(XamlRoot root) => root.Content is FrameworkElement element ? element.ActualTheme : ElementTheme.Default;
}
