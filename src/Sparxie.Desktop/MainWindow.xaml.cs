using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
        TrySetWindowIcon();
        AppWindow.Closing += OnAppWindowClosing;
        _viewModel.Settings.Saved += ApplyCloseBehavior;
        ApplyCloseBehavior();
        Activated += OnFirstActivated;

        _viewModel.Home.PropertyChanged += OnHomePropertyChanged;
        _viewModel.Home.GameRunning += OnGameRunning;
        // 缓存背景在 HomeViewModel 构造期就同步设置了，早于这里订阅；补一次，否则启动时背景与主题色不会生效。
        if (_viewModel.Home.BackgroundPath is { } initial)
        {
            UpdateWindowBackground(initial);
        }
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

        _viewModel.Home.NotifyGameIconsLoaded();
    }

    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.BackgroundPath))
        {
            UpdateWindowBackground(_viewModel.Home.BackgroundPath);
        }
    }

    private Storyboard? _fade;

    private void UpdateWindowBackground(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WindowBackground.Source = null;
            WindowBackground.Visibility = Visibility.Collapsed;
            WindowBackgroundFade.Opacity = 0;
            return;
        }

        try
        {
            var next = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            _ = ApplyAccentAsync(path);
            if (WindowBackground.Source is null)
            {
                WindowBackground.Source = next;
                WindowBackground.Visibility = Visibility.Visible;
                return;
            }

            // 新图叠在旧图之上淡入，结束后换到底层，避免切换游戏时硬切。
            _fade?.Stop();
            WindowBackgroundFade.Source = next;
            WindowBackgroundFade.Opacity = 0;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, WindowBackgroundFade);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                WindowBackground.Source = next;
                WindowBackgroundFade.Opacity = 0;
            };
            _fade = storyboard;
            storyboard.Begin();
        }
        catch
        {
            WindowBackground.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>按背景图主色更新启动按钮：画刷是应用级资源，改 Color 即时生效。</summary>
    private async Task ApplyAccentAsync(string path)
    {
        try
        {
            await ApplyAccentCoreAsync(path);
        }
        catch (Exception ex)
        {
            DesktopLog.Write("应用主题色失败", ex);
        }
    }

    private async Task ApplyAccentCoreAsync(string path)
    {
        var color = await AccentColorExtractor.ExtractAsync(path).ConfigureAwait(true);
        DesktopLog.Write($"主题色 {color} ({path})");
        if (Application.Current.Resources["LaunchAccentBrush"] is SolidColorBrush accent)
        {
            accent.Color = color;
        }

        // 悬停提亮 12%、按下压暗 15%（与系统强调色的 Light1 / 深色变体同思路），保证悬停态一眼能看出。
        SetStartButtonColors(color);

        if (Application.Current.Resources["LaunchAccentHoverBrush"] is SolidColorBrush hover)
        {
            hover.Color = Mix(color, Colors.White, 0.88);
            // 悬停色更亮，文字色必须按悬停色重新选，否则白字压在浅色上看不清。
            if (Application.Current.Resources["LaunchAccentHoverForegroundBrush"] is SolidColorBrush hoverFore)
            {
                var hoverLuma = (0.299 * hover.Color.R + 0.587 * hover.Color.G + 0.114 * hover.Color.B) / 255.0;
                hoverFore.Color = hoverLuma > 0.55 ? Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1A) : Colors.White;
            }
        }

        if (Application.Current.Resources["LaunchAccentPressedBrush"] is SolidColorBrush pressed)
        {
            pressed.Color = Mix(color, Colors.Black, 0.85);
        }

        ApplySystemAccent(color);

        // 相对亮度高的主色配深色字，否则白字。
        var luma = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        if (Application.Current.Resources["LaunchAccentForegroundBrush"] is SolidColorBrush fore)
        {
            fore.Color = luma > 0.62 ? Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1A) : Colors.White;
        }
    }

    /// <summary>
    /// 覆盖 SystemAccentColor 及其亮/暗变体（按 20% 递进与白/黑混合），
    /// 系统强调色按钮的悬停、按下色由它们派生；随后翻转一次 RequestedTheme 让 ThemeResource 重新取值。
    /// </summary>
    private void ApplySystemAccent(Windows.UI.Color color)
    {
        var res = Application.Current.Resources;
        res["SystemAccentColor"] = color;
        for (var i = 1; i <= 3; i++)
        {
            var percent = 1 - 0.2 * i;
            res[$"SystemAccentColorLight{i}"] = Mix(color, Colors.White, percent);
            res[$"SystemAccentColorDark{i}"] = Mix(color, Colors.Black, percent);
        }

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = ElementTheme.Light;
            root.RequestedTheme = ElementTheme.Default;
        }
    }

    private static void SetStartButtonColors(Windows.UI.Color back)
    {
        var res = Application.Current.Resources;
        void Set(string key, Windows.UI.Color c)
        {
            if (res[key] is SolidColorBrush brush)
            {
                brush.Color = c;
            }
        }

        Set("StartButtonBrush", AccentColorExtractor.WithSv(back, 0.42, 0.97));
        Set("StartButtonHoverBrush", AccentColorExtractor.WithSv(back, 0.52, 0.90));
        Set("StartButtonPressedBrush", AccentColorExtractor.WithSv(back, 0.58, 0.80));
    }

    private static Windows.UI.Color Mix(Windows.UI.Color input, Windows.UI.Color blend, double percent) =>
        Windows.UI.Color.FromArgb(
            255,
            (byte)(input.R * percent + blend.R * (1 - percent)),
            (byte)(input.G * percent + blend.G * (1 - percent)),
            (byte)(input.B * percent + blend.B * (1 - percent)));

    /// <summary>
    /// Esc：设置打开时关设置，在设置页时返回主页。用按键事件而不是 KeyboardAccelerator——
    /// 后者会让框架给相关控件的 ToolTip 追加“Esc”快捷键提示。
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.IsProfilesVisible)
        {
            _viewModel.ShowHomeCommand.Execute(null);
            e.Handled = true;
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

    /// <summary>窗口 / 任务栏图标；ico 随程序一起复制，缺失时回退到 exe 自带图标。</summary>
    private void TrySetWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "sparxie.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }

            // 自绘标题栏左侧的图标（系统标题栏图标被 ExtendsContentIntoTitleBar 取代，需要自己放）。
            var png = Path.Combine(AppContext.BaseDirectory, "sparxie-64.png");
            if (File.Exists(png))
            {
                TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(png));
            }
        }
        catch (Exception ex)
        {
            DesktopLog.Write("设置窗口图标失败", ex);
        }
    }

    /// <summary>“游戏启动后”设置为最小化时，游戏进入运行就把主窗口最小化。</summary>
    private void OnGameRunning()
    {
        if (_viewModel.Settings.CurrentAfterLaunch == AfterLaunchBehavior.Minimize
            && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _viewModel.Profiles.Flush();
        _tray?.Dispose();
        await _launcher.DisposeAsync();
    }

    // ---- 关闭按钮行为：退出前确认 / 最小化到托盘 / 直接退出 ----

    private TrayIcon? _tray;
    private bool _forceClose;
    private bool _dialogOpen;

    /// <summary>托盘图标始终显示（与关闭行为无关）：启动时创建，退出时才移除。</summary>
    private void ApplyCloseBehavior()
    {
        _tray ??= new TrayIcon("Sparxie", ShowFromTray, ExitApplication);
        _tray.Show();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose)
        {
            return;
        }

        switch (_viewModel.Settings.CurrentCloseBehavior)
        {
            case CloseBehavior.Exit:
                return;
            case CloseBehavior.Tray:
                args.Cancel = true;
                HideToTray();
                return;
            default:
                args.Cancel = true;
                _ = ConfirmExitAsync();
                return;
        }
    }

    private void HideToTray()
    {
        // 隐藏窗口前必须有托盘图标，否则无法恢复（正常情况下图标一直都在，这里兜底）。
        ApplyCloseBehavior();
        AppWindow.Hide();
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void ExitApplication()
    {
        _forceClose = true;
        _viewModel.Profiles.Flush();
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    private async Task ConfirmExitAsync()
    {
        if (_dialogOpen || Content?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            // 不自定义 MinHeight：加高后复选框方块与文字会上下错位；默认 32px 高，文字与方块对齐。
            var remember = new CheckBox { Content = "记住本次选择，下次不再询问", VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 0, 0) };
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "退出 Sparxie？",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "退出后会关闭启动器，已经启动的游戏不受影响。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        remember,
                    },
                },
                PrimaryButtonText = "退出",
                SecondaryButtonText = "最小化到托盘",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Primary:
                    Remember(remember, CloseBehavior.Exit);
                    ExitApplication();
                    break;
                case ContentDialogResult.Secondary:
                    Remember(remember, CloseBehavior.Tray);
                    HideToTray();
                    break;
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>勾选“记住本次选择”时把选择写入设置（之后点关闭不再询问；可在应用设置里改回）。</summary>
    private void Remember(CheckBox remember, CloseBehavior choice)
    {
        if (remember.IsChecked == true)
        {
            _viewModel.Settings.CloseBehaviorIndex = (int)choice;
        }
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
        // 系统最小化/最大化/关闭按钮压在壁纸上，默认前景色随主题可能看不清：统一白色图标，悬停/按下给半透明白底。
        appWindow.TitleBar.ButtonForegroundColor = Colors.White;
        appWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;
        appWindow.TitleBar.ButtonPressedForegroundColor = Colors.White;
        appWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
        appWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        appWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);

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
