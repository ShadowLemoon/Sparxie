using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Config.Tests;

public sealed class GameIdConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-gameid-tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public GameIdConfigTests() => Directory.CreateDirectory(_dir);

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

    private static string ConfigWithGame(string game) => $$"""
        {
          "schemaVersion": 1,
          "selectedProfileId": "p1",
          "profiles": [
            { "id": "p1", "displayName": "x", "game": "{{game}}", "executablePath": "" }
          ]
        }
        """;

    [Fact]
    public void 内置游戏配置读取后再保存字节不变()
    {
        var store = new AppConfigStore(ConfigPath);
        store.Save(new AppConfig
        {
            SelectedProfileId = "g",
            Profiles =
            [
                new GameProfile { Id = "g", DisplayName = "原神", Game = GameIds.Genshin, ExecutablePath = @"D:\G\YuanShen.exe", Hoyo = new HoyoProfileSettings() },
                new GameProfile { Id = "s", DisplayName = "星铁", Game = GameIds.StarRail, ExecutablePath = @"D:\S\StarRail.exe", Hoyo = new HoyoProfileSettings() },
                new GameProfile { Id = "z", DisplayName = "绝区零", Game = GameIds.ZenlessZoneZero, ExecutablePath = @"D:\Z\ZenlessZoneZero.exe" },
            ],
        });
        var before = File.ReadAllBytes(ConfigPath);
        var text = System.Text.Encoding.UTF8.GetString(before);
        Assert.Contains("\"game\": \"genshin\"", text);
        Assert.Contains("\"game\": \"starRail\"", text);
        Assert.Contains("\"game\": \"zenlessZoneZero\"", text);

        var loaded = store.Load();
        Assert.Equal(ConfigLoadState.Loaded, loaded.State);
        store.Save(loaded.Config);

        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void 未知但语法合法的第三方id不触发配置重置()
    {
        File.WriteAllText(ConfigPath, ConfigWithGame("ext.acme.demo"));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Null(result.BackupPath);
        Assert.Equal("ext.acme.demo", Assert.Single(result.Config.Profiles).Game);
        Assert.Empty(Directory.GetFiles(_dir, "config.invalid-*"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("ext.a.b")]
    [InlineData("Ext.acme.demo")]
    [InlineData("")]
    [InlineData("unknownGame")]
    public void id语法非法视为配置损坏(string game)
    {
        File.WriteAllText(ConfigPath, ConfigWithGame(game));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.RestoredFromCorrupt, result.State);
    }

    [Fact]
    public void 历史PascalCase的game值读取后规范化()
    {
        File.WriteAllText(ConfigPath, ConfigWithGame("StarRail"));

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        Assert.Equal(GameIds.StarRail, result.Config.Profiles[0].Game);
    }
}
