using System.Text.Json;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Config.Tests;

/// <summary>
/// A client wrote endfieldWidth/Height 0 for "follow the stream", AppConfigValidator rejected the size, and every profile was moved to config.invalid-*.json. These tests keep that file loadable and keep one bad profile from costing the others.
/// </summary>
public class ConfigSurvivalTests : IDisposable
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

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Profile(string id, string game, string exe, string hoyo = "") =>
        "{ \"id\": \"" + id + "\", \"displayName\": \"" + id + "\", \"game\": \"" + game + "\", \"executablePath\": \"" + exe + "\""
        + (hoyo.Length > 0 ? ", \"hoyo\": { " + hoyo + " }" : "") + " }";

    private void Write(params string[] profiles) =>
        File.WriteAllText(ConfigPath, $$"""{ "schemaVersion": 1, "selectedProfileId": null, "profiles": [ {{string.Join(",\n", profiles)}} ] }""");

    [Fact]
    public void 终末地跟随串流的0x0尺寸配置原样读取并保留全部Profile()
    {
        // the file that used to be moved aside because of the 0x0 Endfield size
        File.Copy(Fixture("endfield-follow-stream-0x0.json"), ConfigPath);
        var before = File.ReadAllBytes(ConfigPath);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Null(result.BackupPath);
        Assert.Empty(result.Changes);
        Assert.Equal(7, result.Config.Profiles.Count);
        Assert.Equal(["genshin-cn-1", "starrail-cn-1", "zzz-cn-1", "starrail-cn-2", "genshin-cn-2", "endfield-cn-1", "endfield-cn-2"], result.Config.Profiles.Select(p => p.Id));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Empty(Directory.GetFiles(_dir, "config.invalid-*"));
        Assert.Empty(Directory.GetFiles(_dir, "config.repaired-*"));
        var endfield = result.Config.Profiles[5].Hoyo!;
        Assert.True(endfield.EndfieldAutoResolution);
        Assert.Equal(0, endfield.EndfieldWidth);
    }

    [Fact]
    public void 跟随串流时不检查Endfield尺寸固定尺寸才检查()
    {
        var profile = new GameProfile
        {
            Id = "e", DisplayName = "e", Game = GameType.Endfield, ExecutablePath = @"D:\Games\Endfield.exe",
            Hoyo = new HoyoProfileSettings { EndfieldAutoResolution = true, EndfieldWidth = 0, EndfieldHeight = 0 },
        };
        Assert.Empty(AppConfigValidator.ValidateProfile(profile));
        profile.Hoyo!.EndfieldAutoResolution = false;
        Assert.Contains(AppConfigValidator.ValidateProfile(profile), e => e.Contains("Endfield 分辨率"));
        profile.Hoyo.EndfieldWidth = 2560;
        profile.Hoyo.EndfieldHeight = 1440;
        Assert.Empty(AppConfigValidator.ValidateProfile(profile));
        profile.Hoyo.EndfieldWidth = 319;
        Assert.NotEmpty(AppConfigValidator.ValidateProfile(profile));
    }

    [Fact]
    public void 客户端写入的全部Profile用真实校验器通过()
    {
        // the fixture holds a profile for every game and option a client writes
        File.Copy(Fixture("profile-matrix.json"), ConfigPath);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Empty(result.Changes);
        Assert.Empty(AppConfigValidator.Validate(result.Config));
        using var document = JsonDocument.Parse(File.ReadAllBytes(Fixture("profile-matrix.json")));
        Assert.Equal(document.RootElement.GetProperty("profiles").GetArrayLength(), result.Config.Profiles.Count);
        Assert.All(result.Config.Profiles.Where(p => p.Game == GameType.Endfield), p =>
        {
            Assert.True(p.Hoyo!.EndfieldInputMode is "touch" or "pc");
            Assert.InRange(p.Hoyo.EndfieldRefreshRate, 24, 1000);
            Assert.True(AppConfigValidator.IsEndfieldSizeValid(p.Hoyo.EndfieldWidth, p.Hoyo.EndfieldHeight), $"{p.Id}: a size Sparxie rejects is never written");
        });
    }

    [Fact]
    public void 超范围的字段改回默认值Profile保留并备份原文件()
    {
        Write(
            Profile("ok", "genshin", @"C:\\Games\\YuanShen.exe", "\"targetFps\": 144"),
            Profile("bad", "starRail", @"C:\\Games\\StarRail.exe", "\"targetFps\": 5, \"backgroundFps\": 5000, \"genshinPreset30Fps\": 0, \"genshinTouchUiScalePercent\": 900"),
            Profile("ef", "endfield", @"C:\\Games\\Endfield.exe",
                "\"endfieldAutoResolution\": false, \"endfieldWidth\": 99999, \"endfieldHeight\": 0, \"endfieldRefreshRate\": 5, \"endfieldInputMode\": \"pc\""));
        var original = File.ReadAllBytes(ConfigPath);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Repaired, result.State);
        Assert.Equal(3, result.Config.Profiles.Count);
        Assert.Equal(144, result.Config.Profiles[0].Hoyo!.TargetFps);
        var bad = result.Config.Profiles[1].Hoyo!;
        Assert.Equal((120, 10, 60, 400), (bad.TargetFps, bad.BackgroundFps, bad.GenshinPreset30Fps, bad.GenshinTouchUiScalePercent));
        var ef = result.Config.Profiles[2].Hoyo!;
        Assert.True(ef.EndfieldAutoResolution);
        Assert.Equal((1920, 1080, 60, "pc"), (ef.EndfieldWidth, ef.EndfieldHeight, ef.EndfieldRefreshRate, ef.EndfieldInputMode));
        Assert.True(result.Changes.Count >= 7, string.Join("\n", result.Changes));
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath!));
        // the repaired file passes the validator and loads without further changes
        Assert.Empty(AppConfigValidator.Validate(result.Config));
        var again = new AppConfigStore(ConfigPath).Load();
        Assert.Equal(ConfigLoadState.Loaded, again.State);
        Assert.Equal(3, again.Config.Profiles.Count);
    }

    [Fact]
    public void 类型错误的字段只丢这个字段Profile保留()
    {
        Write(Profile("p", "genshin", @"C:\\Games\\YuanShen.exe", "\"targetFps\": \"fast\", \"processPriority\": \"turbo\", \"fpsUnlockEnabled\": false, \"backgroundFps\": 20"));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Repaired, result.State);
        var hoyo = Assert.Single(result.Config.Profiles).Hoyo!;
        Assert.Equal(120, hoyo.TargetFps);
        Assert.Equal(ProcessPriority.Normal, hoyo.ProcessPriority);
        Assert.False(hoyo.FpsUnlockEnabled);
        Assert.Equal(20, hoyo.BackgroundFps);
    }

    [Fact]
    public void 绝区零带hoyo段时移除hoyo缺displayName时用id()
    {
        Write(
            Profile("zzz", "zenlessZoneZero", @"C:\\Games\\ZenlessZoneZero.exe", "\"targetFps\": 60"),
            """{ "id": "noname", "game": "starRail", "executablePath": "C:\\Games\\StarRail.exe" }""");

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Repaired, result.State);
        Assert.Equal(2, result.Config.Profiles.Count);
        Assert.Null(result.Config.Profiles[0].Hoyo);
        Assert.Equal("noname", result.Config.Profiles[1].DisplayName);
    }

    [Fact]
    public void 无法修复的Profile被移出其余保留()
    {
        Write(
            Profile("keep1", "genshin", @"C:\\Games\\YuanShen.exe"),
            Profile("evil", "genshin", @"C:\\Windows\\notepad.exe"),
            "\"not an object\"",
            """{ "id": 7, "displayName": "x", "game": "genshin", "executablePath": "" }""",
            Profile("keep2", "endfield", @"C:\\Games\\Endfield.exe"));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Repaired, result.State);
        Assert.Equal(["keep1", "keep2"], result.Config.Profiles.Select(p => p.Id));
        Assert.Equal(3, result.Changes.Count(c => c.Contains("移出")));
        Assert.Single(Directory.GetFiles(_dir, "config.repaired-*"));
    }

    [Fact]
    public void JSON损坏但有可读备份时恢复备份而不是生成空白()
    {
        // the copy a client leaves before each of its writes
        File.WriteAllText(Path.Combine(_dir, "config.backup-20260101T094312Z.json"), File.ReadAllText(Fixture("endfield-follow-stream-0x0.json")));
        File.WriteAllText(ConfigPath, "{ \"schemaVersion\": 1, \"profiles\": [ { broken");
        var broken = File.ReadAllBytes(ConfigPath);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.RestoredFromBackup, result.State);
        Assert.Equal(7, result.Config.Profiles.Count);
        Assert.EndsWith("config.backup-20260101T094312Z.json", result.RecoveredFrom);
        Assert.Equal(broken, File.ReadAllBytes(result.BackupPath!));
        Assert.Equal(7, new AppConfigStore(ConfigPath).Load().Config.Profiles.Count);
    }

    [Fact]
    public void JSON损坏且没有可读备份时生成空白配置并保留原文件()
    {
        File.WriteAllText(Path.Combine(_dir, "config.backup-20260101T000000Z.json"), "also broken");
        File.WriteAllText(ConfigPath, "nope");

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.RestoredFromCorrupt, result.State);
        Assert.Empty(result.Config.Profiles);
        Assert.Equal("nope", File.ReadAllText(result.BackupPath!));
    }

    [Fact]
    public void 空白配置旁有带Profile的备份时只提示不自动恢复()
    {
        File.WriteAllText(ConfigPath, """{ "schemaVersion": 1, "selectedProfileId": null, "profiles": [] }""");
        File.Copy(Fixture("endfield-follow-stream-0x0.json"), Path.Combine(_dir, "config.invalid-20260101095137-d8b536fa.json"));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Empty(result.Config.Profiles);
        Assert.EndsWith("config.invalid-20260101095137-d8b536fa.json", result.RecoverableBackupPath);
        Assert.Contains(result.Changes, c => c.Contains("7 个") && c.Contains("未自动恢复"));
        Assert.Equal("""{ "schemaVersion": 1, "selectedProfileId": null, "profiles": [] }""", File.ReadAllText(ConfigPath));
    }
}
