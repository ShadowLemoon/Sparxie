using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using WinRT.Interop;

namespace Sparxie.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LauncherService _launcher;
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel, LauncherService launcher)
    {
        InitializeComponent();
        _launcher = launcher;
        _viewModel = viewModel;
        if (Content is FrameworkElement root)
        {
            root.DataContext = viewModel;
        }
        Title = "Sparxie";
        Closed += OnClosed;
        TryConfigureWindow();
        Activated += OnFirstActivated;

        _viewModel.Home.PropertyChanged += OnHomePropertyChanged;
        SyncGameTabs();
        _ = LoadGameIconsAsync();
    }

    private async Task LoadGameIconsAsync()
    {
        var service = new Services.GameBackgroundService();
        foreach (GameType game in Enum.GetValues<GameType>())
        {
            try
            {
                var path = await service.GetGameIconPathAsync(game).ConfigureAwait(true);
                if (path is not null)
                {
                    Services.GameIconCache.Set(game, path);
                }
            }
            catch
            {
                // 图标获取失败时标签退化为纯文字。
            }
        }

        IconGenshin.Source = IconSource(GameType.Genshin);
        IconStarRail.Source = IconSource(GameType.StarRail);
        IconZenlessZoneZero.Source = IconSource(GameType.ZenlessZoneZero);
    }

    private static Microsoft.UI.Xaml.Media.ImageSource? IconSource(GameType game) =>
        Services.GameIconCache.Get(game) is { } path
            ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path))
            : null;

    private bool _syncingTabs;

    private void OnGameTabChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<GameType>(tag, out var game))
        {
            _viewModel.Home.SelectGameCommand.Execute(game);
        }
    }

    private void SyncGameTabs()
    {
        _syncingTabs = true;
        ApplyTabState(TabGenshin, _viewModel.Home.SelectedGame == GameType.Genshin);
        ApplyTabState(TabStarRail, _viewModel.Home.SelectedGame == GameType.StarRail);
        ApplyTabState(TabZenlessZoneZero, _viewModel.Home.SelectedGame == GameType.ZenlessZoneZero);
        _syncingTabs = false;
    }

    /// <summary>选中态视觉由代码驱动（白胶囊+深色字），避免依赖模板 CheckStates。</summary>
    private static void ApplyTabState(ToggleButton button, bool selected)
    {
        button.IsChecked = selected;
        button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            selected ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            selected ? Windows.UI.Color.FromArgb(0xFF, 0x17, 0x18, 0x1A) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.SelectedGame))
        {
            SyncGameTabs();
        }
        else if (e.PropertyName == nameof(HomeViewModel.BackgroundPath))
        {
            UpdateWindowBackground(_viewModel.Home.BackgroundPath);
        }
    }

    private void UpdateWindowBackground(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WindowBackground.Source = null;
            WindowBackground.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            WindowBackground.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            WindowBackground.Visibility = Visibility.Visible;
        }
        catch
        {
            WindowBackground.Visibility = Visibility.Collapsed;
        }
    }

    private bool _sized;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        // 系统会在激活时恢复上次保存的窗口摆放，覆盖构造期设置；延迟到恢复完成后再按 DPI 定尺寸。
        if (_sized)
        {
            return;
        }

        _sized = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var hwnd = WindowNative.GetWindowHandle(this);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            var scale = GetDpiForWindow(hwnd) / 96.0;
            appWindow.Resize(new Windows.Graphics.SizeInt32
            {
                Width = (int)Math.Round(1280 * scale),
                Height = (int)Math.Round(800 * scale),
            });
            ApplyMinimumVisibleSize(hwnd, appWindow);
        };
        timer.Start();
    }

    /// <summary>PreferredMinimum* 作用于含隐形调整边框的外框；按 DWM 可见边界补偿，使可见宽度下限达到目标值。</summary>
    private void ApplyMinimumVisibleSize(nint hwnd, AppWindow appWindow)
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var outer = new Win32Rect();
        _ = GetWindowRect(hwnd, ref outer);
        var visible = new DwmRect();
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, ref visible, sizeof(int) * 4) != 0)
        {
            return;
        }

        var padX = Math.Max(0, (outer.Right - outer.Left) - (visible.Right - visible.Left));
        var padY = Math.Max(0, (outer.Bottom - outer.Top) - (visible.Bottom - visible.Top));
        // +10 覆盖系统对最小跟踪尺寸的取整损耗，保证可见下限不低于目标。
        presenter.PreferredMinimumWidth = 1650 + padX + 10;
        presenter.PreferredMinimumHeight = 1000 + padY;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        await _launcher.DisposeAsync();
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) =>
        _viewModel.CloseSettingsCommand.Execute(null);

    private void TryConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            // 物理像素；避免拖拽过窄导致 Profile 表单与左栏挤压。
            presenter.PreferredMinimumWidth = 1650;
            presenter.PreferredMinimumHeight = 1000;
        }

        // AppWindow.Resize 使用物理像素；按 DPI 换算，保证逻辑尺寸恒为 1280x800。
        var scale = GetDpiForWindow(hwnd) / 96.0;
        appWindow.Resize(new Windows.Graphics.SizeInt32
        {
            Width = (int)Math.Round(1280 * scale),
            Height = (int)Math.Round(800 * scale),
        });
    }

    [System.Runtime.InteropServices.LibraryImport("User32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.LibraryImport("User32.dll", EntryPoint = "GetWindowRect")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, ref Win32Rect rect);

    [System.Runtime.InteropServices.LibraryImport("Dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, ref DwmRect rect, int size);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DwmRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
