using Microsoft.UI.Xaml.Controls;

namespace Sparxie.Desktop.Views;

public sealed partial class HomePage : Page
{
    public HomePage() => InitializeComponent();

    private void CopyLaunchErrorClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel { Home: { HasLaunchError: true } home })
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(home.LaunchErrorDiagnostics);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _touchHideTimer;

    private void BackgroundStripPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        BackgroundSwitchStrip.Opacity = 1;
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            RestartTouchHideTimer();
        }
    }

    private void BackgroundStripPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // 触屏 / 触控笔没有悬停：抬手就会触发 Exited，改由定时器延迟隐藏，否则条会一闪而过。
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            BackgroundSwitchStrip.Opacity = 0;
        }
        else
        {
            RestartTouchHideTimer();
        }
    }

    private void RestartTouchHideTimer()
    {
        if (_touchHideTimer is null)
        {
            _touchHideTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _touchHideTimer.Tick += (_, _) =>
            {
                _touchHideTimer.Stop();
                BackgroundSwitchStrip.Opacity = 0;
            };
        }

        _touchHideTimer.Stop();
        _touchHideTimer.Start();
    }

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
