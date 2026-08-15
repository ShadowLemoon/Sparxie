using System.IO;
using System.Windows;
using Sparxie.App;
using Sparxie.App.Pages;
using Sparxie.App.Rpc;
using Sparxie.Contracts.Models;

var errors = new List<string>();

// STA 线程内创建 WPF 控件
var thread = new Thread(() =>
{
    try
    {
        // 模拟 App.OnStartup 的配置加载（无配置文件时生成空配置）
        var config = new AppConfig();
        var state = new AppState(config, null, new BrokerClient());
        AppStateHolder.Current = state;

        // 1. 构造 MainWindow（含背景层与 InfoBar 逻辑）
        var window = new MainWindow(state);
        window.Measure(new Size(960, 640));
        window.Arrange(new Rect(0, 0, 960, 640));
        Console.WriteLine("[smoke] MainWindow OK");

        // 2. 构造 HomePage（含 Profile 列表、启动、FPS 热调 UI）
        var home = new HomePage();
        home.Measure(new Size(960, 640));
        home.Arrange(new Rect(0, 0, 960, 640));
        Console.WriteLine("[smoke] HomePage OK");

        // 3. 构造 SettingsPage（此前 XAML 初始化期 CheckBox 事件崩溃点）
        var settings = new SettingsPage();
        settings.Measure(new Size(960, 640));
        settings.Arrange(new Rect(0, 0, 960, 640));
        Console.WriteLine("[smoke] SettingsPage OK");

        // 4. 验证带 Hoyo 配置的 Profile 加载路径（LoadProfile 完整分支）
        var p = new GameProfile
        {
            Id = "p1",
            DisplayName = "星铁",
            Game = GameType.StarRail,
            Variant = "cn",
            ExecutablePath = @"D:\Games\StarRail\StarRail.exe",
            Hoyo = new HoyoProfileSettings
            {
                FpsUnlockEnabled = true,
                TargetFps = 120,
                BackgroundFpsLimitEnabled = true,
                BackgroundFps = 10,
                ProcessPriority = ProcessPriority.AboveNormal,
            },
        };
        state.AddProfile(p);
        Console.WriteLine("[smoke] AddProfile OK, count=" + state.Config.Profiles.Count);

        // 触发页面 Loaded 逻辑（刷新列表）
        RaiseLoaded(home);
        RaiseLoaded(settings);
        Console.WriteLine("[smoke] Loaded handlers OK");

        // 5. 验证事件链：新增 Profile 后主页列表自动同步（ProfilesChanged → RefreshProfiles）
        var p2 = new GameProfile
        {
            Id = "p2",
            DisplayName = "原神国服",
            Game = GameType.Genshin,
            Variant = "cn",
            ExecutablePath = @"D:\Games\Genshin\YuanShen.exe",
        };
        state.AddProfile(p2);
        var homeItems = home.FindName("ProfileList") as System.Windows.Controls.ListBox;
        Console.WriteLine($"[smoke] HomePage list count after add: {homeItems?.Items.Count ?? -1}");

        // 6. 验证选中切换：SelectProfile 触发 SelectedProfileChanged
        state.SelectProfile(p2);
        Console.WriteLine($"[smoke] Selected: {state.SelectedProfile?.DisplayName}, selectedItem: {(homeItems?.SelectedItem as GameProfile)?.DisplayName ?? "null"}");

        // 7. 验证删除后同步
        state.RemoveProfile(p);
        Console.WriteLine($"[smoke] HomePage list count after remove: {homeItems?.Items.Count ?? -1}");

        Console.WriteLine("[smoke] ALL OK");
    }
    catch (Exception ex)
    {
        errors.Add(ex.ToString());
        Console.WriteLine("[smoke] FAIL: " + ex);
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

if (errors.Count > 0)
{
    Console.WriteLine("SMOKE FAILED");
    Environment.Exit(1);
}

Console.WriteLine("SMOKE PASSED");

static void RaiseLoaded(FrameworkElement element)
{
    // 触发 Loaded 事件（页面在未显示状态下不会自动触发）
    var loadedEvent = typeof(FrameworkElement).GetField("LoadedEvent", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    if (loadedEvent?.GetValue(null) is RoutedEvent routedEvent)
    {
        element.RaiseEvent(new RoutedEventArgs(routedEvent));
    }
}
