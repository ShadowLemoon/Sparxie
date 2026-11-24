using Microsoft.UI.Xaml;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;

namespace Sparxie.Desktop;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private Window? _window;
    private SingleInstance? _singleInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (sender, e) => DesktopLog.Write("未捕获异常", e.Exception);
        // XAML 之外（线程池、任务）的致命异常也留一条日志，方便定位"打不开"。
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            DesktopLog.Write("进程级未处理异常", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (sender, e) => DesktopLog.Write("未观察到的任务异常", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 同一目录已有实例在运行：通知它显示窗口，本进程直接退出。
        _singleInstance = SingleInstance.TryAcquire(AppPaths.DataRoot, () =>
            _window?.DispatcherQueue.TryEnqueue(() => (_window as MainWindow)?.BringToFront()));
        if (_singleInstance is null)
        {
            Environment.Exit(0);
            return;
        }

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

        GameDirectory.Reload(); // 先读出已确认的插件游戏，主页和 Profile 校验才认识它们
        var launcher = new LauncherService();
        var settingsPanel = new SettingsPanelState();
        var viewModel = new MainWindowViewModel(
            new HomeViewModel(data, launcher),
            new ProfileManagerViewModel(data),
            new SettingsViewModel(data),
            settingsPanel);
        if (!AppPaths.IsWritable)
        {
            // 绿色便携：不悄悄把数据写到 AppData。写不进程序目录时明确告诉用户，由用户决定怎么处理。
            viewModel.Home.ShowStartupProblem(new LaunchErrorInfo(
                "程序目录不可写，配置、日志和缓存都无法保存",
                "Sparxie 装在需要管理员权限的位置。请以管理员身份运行 Sparxie，或把整个文件夹移到可写的位置（例如其它盘的普通目录）。",
                AppPaths.DataRoot));
        }

        var window = new MainWindow(viewModel, launcher);
        _window = window;
        MainWindow = window;
        window.Activate();
    }
}
