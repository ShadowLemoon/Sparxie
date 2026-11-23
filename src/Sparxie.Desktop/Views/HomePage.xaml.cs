using Microsoft.UI.Xaml.Controls;

namespace Sparxie.Desktop.Views;

public sealed partial class HomePage : Page
{
    public HomePage() => InitializeComponent();

    /// <summary>屏幕矮时限制 Flyout 内容高度，列表在上方可用空间内滚动，避免被挤到按钮侧边。</summary>
    private void SwitcherFlyoutOpening(object? sender, object e)
    {
        var top = SwitcherButton.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        SwitcherScroll.MaxHeight = Math.Max(120, top - 34);
    }

    private void CopyLaunchErrorClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel { Home: { HasLaunchError: true } home })
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(home.LaunchErrorDiagnostics);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private void BackgroundStripPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => BackgroundSwitchStrip.Opacity = 1;

    private void BackgroundStripPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => BackgroundSwitchStrip.Opacity = 0;

    /// <summary>滚轮在底部条上切换背景（向上 = 上一张）。</summary>
    private void BackgroundStripWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel { Home: { } home })
        {
            return;
        }

        var delta = e.GetCurrentPoint(BackgroundSwitchStrip).Properties.MouseWheelDelta;
        var current = home.BackgroundDots.ToList().FindIndex(dot => dot.IsSelected);
        if (delta != 0 && current >= 0)
        {
            home.SelectBackgroundCommand.Execute(current + (delta > 0 ? -1 : 1));
        }

        e.Handled = true;
    }

    private void BackgroundDotClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.Home.SelectBackgroundCommand.Execute(index);
        }
    }
}
