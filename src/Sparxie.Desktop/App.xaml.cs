using Microsoft.UI.Xaml;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;

namespace Sparxie.Desktop;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (sender, e) => DesktopLog.Write("未捕获异常", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var data = new DesktopDataService();
        try
        {
            data.Load();
        }
        catch (Exception ex)
        {
            // Keep the shell usable enough to show the failure instead of crashing before a window exists.
            DesktopLog.Write("配置加载失败", ex);
        }

        var launcher = new LauncherService();
        var settingsPanel = new SettingsPanelState();
        var viewModel = new MainWindowViewModel(
            new HomeViewModel(data, launcher),
            new ProfileManagerViewModel(data),
            new SettingsViewModel(data),
            settingsPanel);
        var window = new MainWindow(viewModel, launcher);
        _window = window;
        MainWindow = window;
        window.Activate();
    }
}
