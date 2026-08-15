using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Config.Tests;

public class AppConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public AppConfigStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

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

    [Fact]
    public void 无配置时生成空白当前schema配置()
    {
        var store = new AppConfigStore(ConfigPath);
        var result = store.Load();

        Assert.Equal(ConfigLoadState.CreatedNew, result.State);
        Assert.Null(result.BackupPath);
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Null(result.Config.SelectedProfileId);
        Assert.Empty(result.Config.Profiles);
    }

    [Fact]
    public void 正常配置可读取()
    {
        var store = new AppConfigStore(ConfigPath);
        var config = new AppConfig
        {
            SelectedProfileId = "p1",
            Profiles =
            [
                new GameProfile
                {
                    Id = "p1",
                    DisplayName = "原神 · 国服",
                    Game = GameType.Genshin,
                    Variant = "cn",
                    ExecutablePath = @"D:\Games\Genshin Impact Game\YuanShen.exe",
                    Hoyo = new HoyoProfileSettings { TargetFps = 120 },
                },
            ],
        };
        store.Save(config);

        var result = store.Load();
        Assert.Equal(ConfigLoadState.Loaded, result.State);
        var profile = Assert.Single(result.Config.Profiles);
        Assert.Equal("p1", profile.Id);
        Assert.Equal(GameType.Genshin, profile.Game);
        Assert.Equal("cn", profile.Variant);
        Assert.Equal(120, profile.Hoyo!.TargetFps);
    }

    [Fact]
    public void JSON损坏时按原始字节备份并生成空白配置()
    {
        var corrupt = "{ not valid json \u0000\x01"u8.ToArray();
        File.WriteAllBytes(ConfigPath, corrupt);

        var store = new AppConfigStore(ConfigPath);
        var result = store.Load();

        Assert.Equal(ConfigLoadState.RestoredFromCorrupt, result.State);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(corrupt, File.ReadAllBytes(result.BackupPath));
        Assert.Empty(result.Config.Profiles);
    }

    [Fact]
    public void 未知schema时备份并生成空白配置()
    {
        File.WriteAllText(ConfigPath, """{"schemaVersion": 99, "selectedProfileId": null, "profiles": []}""");

        var store = new AppConfigStore(ConfigPath);
        var result = store.Load();

        Assert.Equal(ConfigLoadState.RestoredFromCorrupt, result.State);
        Assert.NotNull(result.BackupPath);
        Assert.Empty(result.Config.Profiles);
    }

    [Fact]
    public void 非法game枚举时备份并生成空白配置()
    {
        File.WriteAllText(ConfigPath, """
            {
              "schemaVersion": 1,
              "selectedProfileId": null,
              "profiles": [{ "id": "p1", "displayName": "x", "game": "wow", "variant": "cn", "executablePath": "C:\\x.exe" }]
            }
            """);

        var store = new AppConfigStore(ConfigPath);
        var result = store.Load();

        Assert.Equal(ConfigLoadState.RestoredFromCorrupt, result.State);
        Assert.NotNull(result.BackupPath);
        Assert.Empty(result.Config.Profiles);
    }

    [Fact]
    public void 重复profileId时备份并生成空白配置()
    {
        File.WriteAllText(ConfigPath, """
            {
              "schemaVersion": 1,
              "selectedProfileId": null,
              "profiles": [
                { "id": "p1", "displayName": "a", "game": "genshin", "variant": "cn", "executablePath": "C:\\a.exe" },
                { "id": "p1", "displayName": "b", "game": "genshin", "variant": "cn", "executablePath": "C:\\b.exe" }
              ]
            }
            """);

        var store = new AppConfigStore(ConfigPath);
        var result = store.Load();

        Assert.Equal(ConfigLoadState.RestoredFromCorrupt, result.State);
        Assert.NotNull(result.BackupPath);
    }

    [Fact]
    public void 已有异常备份不会被覆盖()
    {
        var corrupt = "garbage"u8.ToArray();
        File.WriteAllBytes(ConfigPath, corrupt);
        var first = new AppConfigStore(ConfigPath).Load();
        Assert.NotNull(first.BackupPath);

        // 再次损坏加载，应生成新的唯一备份，而不是覆盖旧的
        File.WriteAllText(ConfigPath, "still broken");
        var second = new AppConfigStore(ConfigPath).Load();

        Assert.NotNull(second.BackupPath);
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.True(File.Exists(first.BackupPath));
        Assert.True(File.Exists(second.BackupPath));
    }

    [Fact]
    public void 原子保存后可读回且格式为camelCase()
    {
        var store = new AppConfigStore(ConfigPath);
        var config = new AppConfig
        {
            SelectedProfileId = null,
            Profiles =
            [
                new GameProfile
                {
                    Id = "p1",
                    DisplayName = "星铁",
                    Game = GameType.StarRail,
                    Variant = "intl",
                    ExecutablePath = @"D:\Games\StarRail\StarRail.exe",
                    Hoyo = new HoyoProfileSettings { FpsUnlockEnabled = false, TargetFps = 120 },
                },
            ],
        };
        store.Save(config);

        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"game\": \"starRail\"", text);
        Assert.Contains("\"processPriority\": \"normal\"", text);

        var reloaded = new AppConfigStore(ConfigPath).Load();
        Assert.Equal(ConfigLoadState.Loaded, reloaded.State);
        Assert.False(reloaded.Config.Profiles[0].Hoyo!.FpsUnlockEnabled);
    }

    [Fact]
    public void 范围校验失败时拒绝保存()
    {
        var store = new AppConfigStore(ConfigPath);
        var config = new AppConfig
        {
            Profiles =
            [
                new GameProfile
                {
                    Id = "p1",
                    DisplayName = "x",
                    Game = GameType.Genshin,
                    Variant = "cn",
                    ExecutablePath = @"C:\x.exe",
                    Hoyo = new HoyoProfileSettings { TargetFps = 9999 },
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => store.Save(config));
    }

    [Fact]
    public void 非白名单EXE被拒绝保存()
    {
        var store = new AppConfigStore(ConfigPath);
        var config = new AppConfig
        {
            Profiles =
            [
                new GameProfile
                {
                    Id = "p1",
                    DisplayName = "x",
                    Game = GameType.Genshin,
                    Variant = "cn",
                    ExecutablePath = @"C:\Games
otepad.exe",
                    Hoyo = new HoyoProfileSettings { TargetFps = 120 },
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => store.Save(config));
        Assert.Contains("白名单", ex.Message);
    }

    [Fact]
    public void 白名单EXE可通过保存()
    {
        var store = new AppConfigStore(ConfigPath);
        var config = new AppConfig
        {
            Profiles =
            [
                new GameProfile
                {
                    Id = "p1",
                    DisplayName = "x",
                    Game = GameType.ZenlessZoneZero,
                    Variant = "cn",
                    ExecutablePath = @"C:\Games\ZenlessZoneZeroBeta.exe",
                },
            ],
        };

        store.Save(config);
        var reloaded = store.Load();
        Assert.Equal(ConfigLoadState.Loaded, reloaded.State);
    }
}
