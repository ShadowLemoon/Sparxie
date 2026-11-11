using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Processes;

namespace Desktop.Tests;

public sealed class ProfileNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-desktop-names-tests", Guid.NewGuid().ToString("N"));

    public ProfileNamesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private ProfileManagerViewModel CreateManager(GameType scope = GameType.Genshin)
    {
        var data = new DesktopDataService(Path.Combine(_dir, "config.json"));
        data.Load();
        // 传入空的查找环境：不读真实注册表，区服只靠 config.ini / EXE 名 / 目录名识别
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(scope);
        return manager;
    }

    private sealed class EmptyFinder : IGameFinderEnvironment
    {
        public IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories() => [];

        public IEnumerable<(string Name, string Directory)> UninstallInstallDirectories() => [];

        public IEnumerable<string> RecentExecutablePaths() => [];

        public IEnumerable<string> RunningExecutablePaths(string executableName) => [];

        public bool FileExists(string path) => false;

        public IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth) => [];
    }

    private string MakeInstall(string folder, string exeName, string? ini)
    {
        var directory = Path.Combine(_dir, folder);
        Directory.CreateDirectory(directory);
        if (ini is not null)
        {
            File.WriteAllText(Path.Combine(directory, "config.ini"), ini);
        }

        return Path.Combine(directory, exeName);
    }

    [Fact]
    public void 默认名称是中性的配置N不预设区服()
    {
        Assert.Equal("配置 1", ProfileNames.NextDefault([]));
        Assert.Equal("配置 2", ProfileNames.NextDefault(["配置 1"]));
        Assert.Equal("配置 1", ProfileNames.NextDefault(["国服"]));
    }

    [Fact]
    public void 自动名称的判断包含旧版预填的名字()
    {
        Assert.True(ProfileNames.IsAuto("配置 3"));
        Assert.True(ProfileNames.IsAuto("国服"));
        Assert.True(ProfileNames.IsAuto("国际服 2"));
        Assert.True(ProfileNames.IsAuto("Steam"));
        Assert.False(ProfileNames.IsAuto("我的小号"));
        Assert.False(ProfileNames.IsAuto("国服 副本"));
    }

    [Fact]
    public void 重名时加序号()
    {
        Assert.Equal("国服", ProfileNames.Unique("国服", []));
        Assert.Equal("国服 2", ProfileNames.Unique("国服", ["国服"]));
        Assert.Equal("国服 3", ProfileNames.Unique("国服", ["国服", "国服 2"]));
    }

    [Fact]
    public void 新建的配置按顺序叫配置N()
    {
        var manager = CreateManager();
        Assert.Equal("配置 1", manager.SelectedProfile!.DisplayName);

        manager.NewCommand.Execute(null);
        Assert.Equal("配置 2", manager.SelectedProfile!.DisplayName);
    }

    [Fact]
    public void 填入路径后按config_ini识别区服改名()
    {
        var manager = CreateManager();
        var exe = MakeInstall("genshin-global", "GenshinImpact.exe", "[General]\r\ngame_biz=hk4e_global\r\n");

        manager.SelectedProfile!.ExecutablePath = exe;

        Assert.Equal("国际服", manager.SelectedProfile.DisplayName);
    }

    [Fact]
    public void B服靠cps或channel识别()
    {
        var manager = CreateManager();
        var exe = MakeInstall("genshin-b", "YuanShen.exe", "[General]\r\nchannel=14\r\ncps=bilibili\r\n");

        manager.SelectedProfile!.ExecutablePath = exe;

        Assert.Equal("B 服", manager.SelectedProfile.DisplayName);
    }

    [Fact]
    public void 没有config_ini时用EXE名识别原神()
    {
        var manager = CreateManager();

        manager.SelectedProfile!.ExecutablePath = MakeInstall("cn", "YuanShen.exe", null);

        Assert.Equal("国服", manager.SelectedProfile.DisplayName);
    }

    [Fact]
    public void 认不出区服时名称保持不变()
    {
        var manager = CreateManager(GameType.ZenlessZoneZero);

        manager.SelectedProfile!.ExecutablePath = MakeInstall("somewhere", "ZenlessZoneZero.exe", null);

        Assert.Equal("配置 1", manager.SelectedProfile.DisplayName);
    }

    [Fact]
    public void 用户自己起的名字不会被改()
    {
        var manager = CreateManager();
        manager.SelectedProfile!.DisplayName = "我的小号";

        manager.SelectedProfile.ExecutablePath = MakeInstall("cn2", "YuanShen.exe", null);

        Assert.Equal("我的小号", manager.SelectedProfile.DisplayName);
    }

    [Fact]
    public void 同一游戏里区服重名时加序号()
    {
        var manager = CreateManager();
        manager.SelectedProfile!.ExecutablePath = MakeInstall("cn-a", "YuanShen.exe", null);
        manager.NewCommand.Execute(null);

        manager.SelectedProfile!.ExecutablePath = MakeInstall("cn-b", "YuanShen.exe", null);

        Assert.Equal("国服 2", manager.SelectedProfile.DisplayName);
    }

    [Fact]
    public void 不同游戏各自从配置1开始命名()
    {
        var genshin = CreateManager(GameType.Genshin);
        genshin.NewCommand.Execute(null);

        var zzz = CreateManager(GameType.ZenlessZoneZero);

        Assert.Equal("配置 1", zzz.SelectedProfile!.DisplayName);
    }
}
