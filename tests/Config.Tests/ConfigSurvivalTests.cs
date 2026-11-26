using System.Text;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Config.Tests;

/// <summary>一个坏 Profile 不能赔上整份配置；任何改动前原文件按原始字节保留。</summary>
public sealed class ConfigSurvivalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public ConfigSurvivalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    private const string Good = """{ "id": "good", "displayName": "好的", "game": "genshin", "executablePath": "C:\\G\\YuanShen.exe" }""";

    private static string Document(params string[] profiles) =>
        $$"""{ "schemaVersion": 1, "selectedProfileId": "good", "profiles": [ {{string.Join(",\n", profiles)}} ] }""";

    private AppConfigLoadResult Load(params string[] profiles)
    {
        File.WriteAllText(ConfigPath, Document(profiles));
        return new AppConfigStore(ConfigPath).Load();
    }

    [Theory]
    [InlineData("""{ "id": "bad", "displayName": "x", "game": "endfield", "executablePath": "C:\\E\\Endfield.exe" }""")]
    [InlineData("""{ "id": "bad", "displayName": "x", "game": "wow", "executablePath": "C:\\x.exe" }""")]
    [InlineData("""{ "id": "bad", "displayName": "x", "executablePath": "C:\\x.exe" }""")]
    [InlineData("""{ "id": "", "displayName": "x", "game": "genshin", "executablePath": "" }""")]
    [InlineData("""{ "id": "good", "displayName": "重复", "game": "genshin", "executablePath": "" }""")]
    [InlineData("""{ "id": "bad", "id": "bad2", "displayName": "x", "game": "genshin", "executablePath": "" }""")]
    [InlineData("42")]
    [InlineData("null")]
    public void 修不了的Profile单独移出_其余保留_原文件按字节备份(string bad)
    {
        File.WriteAllText(ConfigPath, Document(Good, bad));
        var original = File.ReadAllBytes(ConfigPath);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Repaired, result.State);
        Assert.Equal("good", Assert.Single(result.Config.Profiles).Id);
        Assert.Equal("good", result.Config.SelectedProfileId);
        Assert.Contains("config.repaired-", Path.GetFileName(result.BackupPath));
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath!));
        Assert.Contains(result.Changes, change => change.Contains("已移出"));

        var again = new AppConfigStore(ConfigPath).Load();
        Assert.Equal(ConfigLoadState.Loaded, again.State);
        Assert.Empty(again.Changes);
    }

    [Fact]
    public void 超范围和类型错误的字段改回默认值_其他字段不动()
    {
        var result = Load("""
            { "id": "good", "displayName": "星铁", "game": "starRail", "executablePath": "C:\\S\\StarRail.exe", "disableTouchUi": true,
              "hoyo": { "targetFps": 9999, "backgroundFps": "快", "genshinTouchUiScalePercent": 1, "processPriority": "realtime", "backgroundFpsLimitEnabled": false },
              "launch": { "width": 100, "height": 100, "fullscreen": "yes", "borderless": true, "extraArguments": "-x\n-y" } }
            """);

        Assert.Equal(ConfigLoadState.Repaired, result.State);
        var profile = Assert.Single(result.Config.Profiles);
        var hoyo = profile.Hoyo!;
        Assert.True(profile.DisableTouchUi);
        Assert.False(hoyo.BackgroundFpsLimitEnabled);
        Assert.Equal(120, hoyo.TargetFps);
        Assert.Equal(10, hoyo.BackgroundFps);
        Assert.Equal(400, hoyo.GenshinTouchUiScalePercent);
        Assert.Equal(ProcessPriority.Normal, hoyo.ProcessPriority);
        Assert.True(profile.Launch!.Borderless);
        Assert.Null(profile.Launch.Width);
        Assert.Null(profile.Launch.Height);
        Assert.Null(profile.Launch.Fullscreen);
        Assert.Null(profile.Launch.ExtraArguments);
    }

    [Fact]
    public void 路径不属于该游戏时只清空路径()
    {
        var result = Load("""{ "id": "good", "displayName": "原神", "game": "genshin", "executablePath": "C:\\x\\notepad.exe", "hoyo": { "targetFps": 144 } }""");

        var profile = Assert.Single(result.Config.Profiles);
        Assert.Equal(string.Empty, profile.ExecutablePath);
        Assert.Equal(144, profile.Hoyo!.TargetFps);
        Assert.Contains(result.Changes, change => change.Contains("notepad.exe"));
    }

    [Fact]
    public void 缺名称和路径_绝区零带hoyo_都能修()
    {
        var result = Load("""{ "id": "good", "game": "zenlessZoneZero", "hoyo": { } }""");

        var profile = Assert.Single(result.Config.Profiles);
        Assert.Equal("good", profile.DisplayName);
        Assert.Equal(string.Empty, profile.ExecutablePath);
        Assert.Null(profile.Hoyo);
    }

    [Fact]
    public void 触屏关闭开关正常保留_类型错误时恢复默认()
    {
        var kept = Load("""{ "id": "zzz-pc", "displayName": "绝区零 键鼠", "game": "zenlessZoneZero", "executablePath": "C:\\Z\\ZenlessZoneZero.exe", "disableTouchUi": true }""");
        Assert.Equal(ConfigLoadState.Loaded, kept.State);
        Assert.True(Assert.Single(kept.Config.Profiles).DisableTouchUi);

        var repaired = Load("""{ "id": "zzz-bad", "displayName": "绝区零", "game": "zenlessZoneZero", "executablePath": "C:\\Z\\ZenlessZoneZero.exe", "disableTouchUi": "yes" }""");
        Assert.Equal(ConfigLoadState.Repaired, repaired.State);
        Assert.False(Assert.Single(repaired.Config.Profiles).DisableTouchUi);
        Assert.Contains(repaired.Changes, change => change.Contains("disableTouchUi"));
    }

    [Fact]
    public void 插件游戏原样保留_设置不是对象时只丢设置()
    {
        const string plugin = """{ "id": "p", "displayName": "插件", "game": "ext.acme.demo", "executablePath": "C:\\D\\Demo.exe", "settings": { "width": 99999 } }""";
        var kept = Load(Good, plugin);
        Assert.Equal(ConfigLoadState.Loaded, kept.State);
        Assert.Equal(99999, (int)kept.Config.Profiles[1].Settings!["width"]!);

        var repaired = Load(Good, plugin.Replace("""{ "width": 99999 }""", "[1]"));
        Assert.Equal(ConfigLoadState.Repaired, repaired.State);
        Assert.Null(repaired.Config.Profiles[1].Settings);
    }

    [Fact]
    public void 带BOM的配置正常读取()
    {
        File.WriteAllText(ConfigPath, Document(Good), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Single(result.Config.Profiles);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "schemaVersion": 99, "profiles": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "profiles": {} }""")]
    public void 整份读不了时用最近的可用备份恢复(string broken)
    {
        Backup("config.backup-old.json", Document(Good.Replace("好的", "旧")), minutesAgo: 60);
        Backup("config.backup-new.json", Document(Good.Replace("好的", "新")), minutesAgo: 10);
        Backup("config.repaired-newest-but-empty.json", Document(), minutesAgo: 1);
        Backup("config.invalid-newest-but-broken.json", "garbage", minutesAgo: 0);
        File.WriteAllText(ConfigPath, broken);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.RestoredFromBackup, result.State);
        Assert.Equal("新", Assert.Single(result.Config.Profiles).DisplayName);
        Assert.Equal(broken, File.ReadAllText(result.BackupPath!));
        Assert.Contains(result.Changes, change => change.Contains("config.backup-new.json"));
        Assert.Equal(ConfigLoadState.Loaded, new AppConfigStore(ConfigPath).Load().State);
    }

    [Fact]
    public void 合法的空配置不会被备份填回()
    {
        Backup("config.backup-old.json", Document(Good), minutesAgo: 5);

        var result = Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Empty(result.Config.Profiles);
    }

    private void Backup(string name, string content, int minutesAgo)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }
}
