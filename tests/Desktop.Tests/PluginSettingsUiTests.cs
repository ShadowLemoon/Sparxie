using System.Text.Json.Nodes;
using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;
using Sparxie.LauncherCore;

namespace Desktop.Tests;

/// <summary>第三方插件游戏的设置：按 schema 生成字段、联动显示、自动保存、归一化后持久化并随快照传给 Broker。</summary>
[Collection("GameDirectory")]
public sealed class PluginSettingsUiTests : IDisposable
{
    private const string PluginId = "ext.acme.demo";

    private const string Schema = """
        { "fields": [
          { "key": "inputMode", "label": "输入模式", "type": "enum", "default": "touch",
            "options": [ { "value": "touch", "label": "触屏" }, { "value": "pc", "label": "键鼠" } ] },
          { "key": "autoResolution", "label": "分辨率跟随串流", "type": "bool", "default": true },
          { "key": "width", "label": "宽度", "type": "int", "min": 320, "max": 7680, "default": 1920,
            "showWhen": { "key": "autoResolution", "equals": false } },
          { "key": "uiScale", "label": "界面缩放", "type": "int", "min": 100, "max": 180, "step": 5, "default": 140 }
        ] }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-plugin-settings-tests", Guid.NewGuid().ToString("N"));
    private readonly Func<IReadOnlyList<GameDescriptor>> _originalSource = GameDirectory.PluginSource;
    private bool _installed = true;
    private bool _withSchema = true;

    public PluginSettingsUiTests()
    {
        Directory.CreateDirectory(_dir);
        Reload();
    }

    public void Dispose()
    {
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

    private void Reload()
    {
        GameDirectory.PluginSource = () =>
        {
            if (!_installed)
            {
                return [];
            }

            PluginSettingsSchema.TryParse(_withSchema ? Schema : null, out var schema, out _);
            return [new GameDescriptor(PluginId, "演示游戏", ["Demo.exe"], schema)];
        };
        GameDirectory.Reload();
    }

    private DesktopDataService NewData()
    {
        var data = new DesktopDataService(ConfigPath);
        data.Load();
        return data;
    }

    private static ProfileMutation Create(JsonObject? settings) =>
        new(Id: "p1", DisplayName: "演示", GameId: PluginId, ExecutablePath: @"D:\Games\Demo.exe", PluginSettings: settings);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private AppConfig ReloadConfig() => new AppConfigStore(ConfigPath).Load().Config;

    [Fact]
    public void 创建Profile时设置按schema归一化后保存()
    {
        var data = NewData();

        var profile = data.AddProfile(Create(Obj("""{ "inputMode": "gamepad", "autoResolution": false, "width": 99999, "uiScale": 133, "evil": 1 }""")));

        Assert.Equal("touch", (string)profile.Settings!["inputMode"]!);
        Assert.False((bool)profile.Settings["autoResolution"]!);
        Assert.Equal(7680, (int)profile.Settings["width"]!);
        Assert.Equal(135, (int)profile.Settings["uiScale"]!);
        Assert.False(profile.Settings.ContainsKey("evil"));
        Assert.Equal(7680, (int)ReloadConfig().Profiles.Single().Settings!["width"]!); // 落盘后重新读取一致
    }

    [Fact]
    public void 不带设置的mutation不会清掉已有设置()
    {
        var data = NewData();
        data.AddProfile(Create(Obj("""{ "uiScale": 160 }""")));

        data.UpdateProfile("p1", new ProfileMutation(DisplayName: "改名"));

        Assert.Equal(160, (int)ReloadConfig().Profiles.Single().Settings!["uiScale"]!);
    }

    [Fact]
    public void 插件暂时不可用时更新设置原样保留_插件回来后按schema归一化()
    {
        var data = NewData();
        data.AddProfile(Create(null));
        _installed = false;
        Reload();

        data.UpdateProfile("p1", new ProfileMutation(PluginSettings: Obj("""{ "uiScale": 133, "future": true }""")));

        var stored = ReloadConfig().Profiles.Single().Settings!;
        Assert.Equal(133, (int)stored["uiScale"]!); // 没有 schema 可用：不擅自改用户的值
        Assert.True((bool)stored["future"]!);

        _installed = true;
        Reload();
        data = NewData();
        data.UpdateProfile("p1", new ProfileMutation(PluginSettings: stored));
        Assert.Equal(135, (int)ReloadConfig().Profiles.Single().Settings!["uiScale"]!);
    }

    [Fact]
    public void 内置游戏的Profile不带插件设置()
    {
        var data = NewData();

        var profile = data.AddProfile(new ProfileMutation(
            Id: "g", DisplayName: "原神", Game: GameType.Genshin, ExecutablePath: @"D:\G\YuanShen.exe", PluginSettings: Obj("""{ "a": 1 }""")));

        Assert.Null(profile.Settings);
    }

    [Fact]
    public void 过大的设置被拒绝()
    {
        var data = NewData();
        var big = new JsonObject { ["x"] = new string('a', PluginSettingsSchema.MaxSettingsBytes + 10) };

        Assert.Throws<LauncherException>(() => data.AddProfile(Create(big)));
    }

    [Fact]
    public void 快照里带上归一化后的settingsJson_内置游戏则为空()
    {
        var data = NewData();
        var plugin = data.AddProfile(Create(Obj("""{ "uiScale": 150 }""")));
        var builtin = data.AddProfile(new ProfileMutation(Id: "g", DisplayName: "原神", Game: GameType.Genshin, ExecutablePath: @"D:\G\YuanShen.exe"));

        var snapshot = ProfileSnapshotMapper.Map(plugin);

        Assert.Equal(150, (int)JsonNode.Parse(snapshot.SettingsJson)!["uiScale"]!);
        Assert.Equal(string.Empty, ProfileSnapshotMapper.Map(builtin).SettingsJson);
    }

    [Fact]
    public void 编辑页按schema生成字段_默认值正确_联动字段默认隐藏()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));

        var editor = manager.Profiles.Single();

        Assert.True(editor.HasPluginFields);
        Assert.Equal(["inputMode", "autoResolution", "width", "uiScale"], editor.PluginFields.Select(f => f.Key));
        Assert.True(editor.PluginFields.Single(f => f.Key == "autoResolution").BoolValue);
        Assert.Equal(140, editor.PluginFields.Single(f => f.Key == "uiScale").NumberValue);
        Assert.False(editor.PluginFields.Single(f => f.Key == "width").IsVisible); // 跟随串流时不显示宽度
        Assert.True(editor.PluginFields.Single(f => f.Key == "uiScale").IsVisible);
    }

    [Fact]
    public void 改字段立即联动显示并自动保存()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));
        var editor = manager.Profiles.Single();
        var revision = editor.PluginSettingsRevision;

        editor.PluginFields.Single(f => f.Key == "autoResolution").BoolValue = false;
        editor.PluginFields.Single(f => f.Key == "width").NumberValue = 99999;
        editor.PluginFields.Single(f => f.Key == "inputMode").TextValue = "pc";

        Assert.True(editor.PluginFields.Single(f => f.Key == "width").IsVisible);
        Assert.True(editor.PluginSettingsRevision > revision);
        var saved = ReloadConfig().Profiles.Single().Settings!;
        Assert.False((bool)saved["autoResolution"]!);
        Assert.Equal(7680, (int)saved["width"]!);
        Assert.Equal("pc", (string)saved["inputMode"]!);
    }

    [Fact]
    public void 数字框被清空成NaN时按默认值保存而不是崩溃()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));
        var editor = manager.Profiles.Single();

        editor.PluginFields.Single(f => f.Key == "uiScale").NumberValue = double.NaN;

        Assert.Equal(140, (int)ReloadConfig().Profiles.Single().Settings!["uiScale"]!);
    }

    [Fact]
    public void 重新打开配置页时字段显示已保存的值()
    {
        var data = NewData();
        data.AddProfile(Create(Obj("""{ "inputMode": "pc", "autoResolution": false, "width": 2560, "uiScale": 160 }""")));

        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));
        var editor = manager.Profiles.Single();

        Assert.Equal("pc", editor.PluginFields.Single(f => f.Key == "inputMode").TextValue);
        Assert.False(editor.PluginFields.Single(f => f.Key == "autoResolution").BoolValue);
        Assert.Equal(2560, editor.PluginFields.Single(f => f.Key == "width").NumberValue);
        Assert.True(editor.PluginFields.Single(f => f.Key == "width").IsVisible);
        Assert.Equal(160, editor.PluginFields.Single(f => f.Key == "uiScale").NumberValue);
    }

    [Fact]
    public void 没有schema的插件游戏没有设置字段_内置游戏也没有()
    {
        _withSchema = false;
        Reload();
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());

        manager.SetScope(new GameKey(PluginId));
        Assert.False(manager.Profiles.Single().HasPluginFields);
        Assert.Null(manager.Profiles.Single().ToMutation().PluginSettings);

        manager.SetScope(GameType.Genshin);
        Assert.False(manager.Profiles.Single().HasPluginFields);
    }

    [Fact]
    public void 编辑项换成别的游戏时旧设置字段被清掉()
    {
        var data = NewData();
        var manager = new ProfileManagerViewModel(data, new EmptyFinder());
        manager.SetScope(new GameKey(PluginId));
        var editor = manager.Profiles.Single();
        Assert.True(editor.HasPluginFields);

        editor.Game = GameType.ZenlessZoneZero;

        Assert.False(editor.HasPluginFields);
        Assert.Null(editor.BuildPluginSettings());
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
