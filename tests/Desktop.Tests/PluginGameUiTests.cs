using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;
using Sparxie.LauncherCore;

namespace Desktop.Tests;

/// <summary>
/// 第三方插件游戏在桌面端的呈现：主页游戏标签、Profile 创建与路径检查。
/// <see cref="GameDirectory"/> 与 <see cref="GameCatalogProvider"/> 是进程内的共享状态，所以这些测试串行执行并在结束时恢复。
/// </summary>
[Collection("GameDirectory")]
public sealed class PluginGameUiTests : IDisposable
{
    private const string PluginId = "ext.acme.demo";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-plugin-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly Func<IReadOnlyList<GameDescriptor>> _originalSource = GameDirectory.PluginSource;
    private bool _pluginInstalled;

    public PluginGameUiTests()
    {
        Directory.CreateDirectory(_dir);
        SetPlugin(installed: true);
    }

    public void Dispose()
    {
        GameDirectory.PluginSource = _originalSource;
        GameDirectory.PluginSource = () => [];
        GameDirectory.Reload();
        GameDirectory.PluginSource = _originalSource;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    /// <summary>模拟用户启用/停用插件：改变"已确认的插件游戏"来源并重新读取。</summary>
    private void SetPlugin(bool installed)
    {
        _pluginInstalled = installed;
        GameDirectory.PluginSource = () => _pluginInstalled
            ? [new GameDescriptor(PluginId, "演示游戏", ["Demo.exe"])]
            : [];
        GameDirectory.Reload();
    }

    private DesktopDataService NewData()
    {
        var data = new DesktopDataService(ConfigPath);
        data.Load();
        return data;
    }

    [Fact]
    public void 内置游戏的GameKey与枚举互通且不是第三方()
    {
        GameKey key = GameType.StarRail;

        Assert.Equal("starRail", key.Id);
        Assert.Equal(GameType.StarRail, key.BuiltIn);
        Assert.False(key.IsThirdParty);
        Assert.Equal("崩坏：星穹铁道", key.Display);
    }

    [Fact]
    public void 已确认的插件游戏出现在游戏列表_显示名来自manifest()
    {
        var keys = GameDirectory.Games;

        Assert.Equal(["genshin", "starRail", "zenlessZoneZero", PluginId], keys.Select(key => key.Id));
        var plugin = new GameKey(PluginId);
        Assert.True(plugin.IsThirdParty);
        Assert.Null(plugin.BuiltIn);
        Assert.Equal("演示游戏", plugin.Display);
        Assert.Contains(plugin, GameDirectory.Options);
    }

    [Fact]
    public void 插件停用后从游戏列表消失_重复读取不报告变化()
    {
        SetPlugin(installed: false);

        Assert.DoesNotContain(GameDirectory.Games, key => key.Id == PluginId);
        Assert.Equal(PluginId, new GameKey(PluginId).Display); // 查不到名字时退回 id，不抛异常
        Assert.False(GameDirectory.Reload()); // 来源没变：不报告变化
    }

    [Fact]
    public void 读取插件来源失败时按没有插件游戏处理()
    {
        GameDirectory.PluginSource = () => throw new UnauthorizedAccessException("denied");

        GameDirectory.Reload();

        Assert.Equal(3, GameDirectory.Games.Count);
    }

    [Fact]
    public void Profile可以按id创建插件游戏_路径按manifest白名单校验_且不带Hoyo设置()
    {
        var data = NewData();

        var profile = data.AddProfile(new ProfileMutation(
            Id: "p1", DisplayName: "演示", GameId: PluginId, ExecutablePath: @"D:\Games\Demo\Demo.exe"));

        Assert.Equal(PluginId, profile.Game);
        Assert.Null(profile.BuiltInGame);
        Assert.Null(profile.Hoyo);
        Assert.Equal(PluginId, new AppConfigStore(ConfigPath).Load().Config.Profiles.Single().Game);
    }

    [Fact]
    public void 插件游戏的EXE不在manifest白名单时被拒绝()
    {
        var data = NewData();

        var ex = Assert.Throws<LauncherException>(() => data.AddProfile(new ProfileMutation(
            Id: "p1", DisplayName: "演示", GameId: PluginId, ExecutablePath: @"D:\Games\Other.exe")));

        Assert.Contains("白名单", ex.Message);
    }

    [Fact]
    public void 插件未启用时不能创建该游戏的有路径Profile()
    {
        SetPlugin(installed: false);
        var data = NewData();

        Assert.Throws<LauncherException>(() => data.AddProfile(new ProfileMutation(
            Id: "p1", DisplayName: "演示", GameId: PluginId, ExecutablePath: @"D:\Games\Demo\Demo.exe")));
    }

    [Fact]
    public void 非法的游戏id创建Profile被拒绝()
    {
        var data = NewData();

        Assert.Throws<LauncherException>(() => data.AddProfile(new ProfileMutation(Id: "p1", DisplayName: "x", GameId: "../evil")));
    }

    [Fact]
    public void 内置游戏的Profile创建行为不变()
    {
        var data = NewData();

        var profile = data.AddProfile(new ProfileMutation(Id: "g", DisplayName: "原神", Game: GameType.Genshin, ExecutablePath: @"D:\G\YuanShen.exe"));

        Assert.Equal("genshin", profile.Game);
        Assert.NotNull(profile.Hoyo);
        Assert.Throws<LauncherException>(() => data.AddProfile(new ProfileMutation(
            Id: "bad", DisplayName: "x", Game: GameType.Genshin, ExecutablePath: @"D:\G\Demo.exe"))); // 插件的 EXE 名不能给内置游戏用
    }

    [Fact]
    public void Profile管理页进入插件游戏时自动落占位配置_且按通用游戏处理()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());

        manager.SetScope(new GameKey(PluginId));

        var editor = Assert.Single(manager.Profiles);
        Assert.Equal(PluginId, editor.Game.Id);
        Assert.False(editor.IsHoyoGame);
        Assert.False(editor.IsGenshin);
        Assert.Equal(PluginId, data.Config.Profiles.Single().Game);
        Assert.Null(data.Config.Profiles.Single().Hoyo);
    }

    [Fact]
    public void Profile编辑页对插件游戏按manifest白名单给路径提示()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));
        var editor = manager.Profiles.Single();

        editor.ExecutablePath = @"D:\Games\Other.exe";
        Assert.Contains("Demo.exe", editor.PathHint); // 提示里列出可用文件名

        editor.ExecutablePath = @"D:\Games\Demo.exe";
        Assert.DoesNotContain("不属于", editor.PathHint);
    }

    [Fact]
    public void 插件游戏的编辑项转mutation时携带游戏id且不带Hoyo字段()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));

        var mutation = manager.Profiles.Single().ToMutation();

        Assert.Equal(PluginId, mutation.GameId);
        Assert.Null(mutation.Game);
        Assert.Null(mutation.FpsUnlockEnabled);
        Assert.Null(mutation.TargetFps);
    }

    [Fact]
    public void 插件游戏的自动查找走通用来源_没找到时给出明确提示()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));

        var outcome = manager.AutoFindExecutableAsync().GetAwaiter().GetResult();

        Assert.Empty(outcome.Choices);
        Assert.Contains("没有找到", outcome.Message);
    }

    [Fact]
    public void 主页的游戏标签包含插件游戏_并能选中_停用后回落到内置游戏()
    {
        var data = NewData();
        data.AddProfile(new ProfileMutation(Id: "p1", DisplayName: "演示配置", GameId: PluginId, ExecutablePath: @"D:\Games\Demo\Demo.exe"));
        data.Preferences.SelectedGame = PluginId;
        data.SavePreferences();

        var home = new HomeViewModel(data, new LauncherService());

        Assert.Equal(PluginId, home.SelectedGame.Id);
        Assert.Contains(home.GameEntries, entry => entry.Game.Id == PluginId && entry.Title == "演示游戏");
        Assert.Equal("p1", home.SelectedProfile?.Id);
        Assert.Equal("演示游戏", home.SwitcherText);

        SetPlugin(installed: false);
        home.Refresh();

        Assert.NotEqual(PluginId, home.SelectedGame.Id);
        Assert.DoesNotContain(home.GameEntries, entry => entry.Game.Id == PluginId);
        Assert.Single(data.Config.Profiles); // 配置保留，只是游戏不可选
    }

    [Fact]
    public void 配置里有插件游戏的配置但插件当前不可用_启动路径不抛异常_配置原样保留()
    {
        // 复现用户的场景：config.json 里已有终末地的配置和 selectedGame，随后插件文件被换成别的版本（哈希变了/不兼容），目录里不再有这款游戏。
        var first = NewData();
        first.AddProfile(new ProfileMutation(Id: "p1", DisplayName: "演示配置", GameId: PluginId, ExecutablePath: @"D:\Games\Demo\Demo.exe"));
        first.Preferences.SelectedGame = PluginId;
        first.Preferences.LastProfileByGame[PluginId] = "p1";
        first.SavePreferences();
        SetPlugin(installed: false);

        var data = NewData();
        var home = new HomeViewModel(data, new LauncherService());
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(home.SelectedGame);
        home.Refresh();

        Assert.NotEqual(PluginId, home.SelectedGame.Id);
        Assert.Contains(data.Config.Profiles, profile => profile.Game == PluginId);
    }

    [Fact]
    public void 主页选中内置游戏的偏好键仍兼容旧版PascalCase()
    {
        var data = NewData();
        data.Preferences.SelectedGame = "StarRail";
        data.SavePreferences();

        var home = new HomeViewModel(data, new LauncherService());

        Assert.Equal("starRail", home.SelectedGame.Id);
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
}
