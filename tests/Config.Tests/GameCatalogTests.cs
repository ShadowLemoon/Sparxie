using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

public class GameCatalogTests
{
    [Theory]
    [InlineData(GameType.Genshin, "genshin")]
    [InlineData(GameType.StarRail, "starRail")]
    [InlineData(GameType.ZenlessZoneZero, "zenlessZoneZero")]
    public void 内置游戏id与config序列化值一致(GameType game, string id)
    {
        Assert.Equal(id, GameIds.ToId(game));
        // config.json 现在存的就是 camelCase 枚举名，id 必须与之逐字相同
        var json = System.Text.Json.JsonSerializer.Serialize(game, Sparxie.Infrastructure.Configuration.ConfigJsonOptions.Serialize);
        Assert.Equal($"\"{id}\"", json);
    }

    [Theory]
    [InlineData("Genshin", "genshin")]
    [InlineData("genshin", "genshin")]
    [InlineData("StarRail", "starRail")]
    [InlineData("ZenlessZoneZero", "zenlessZoneZero")]
    [InlineData("ext.vendor.game", "ext.vendor.game")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalize兼容PascalCase且保留非内置id(string? input, string? expected)
    {
        Assert.Equal(expected, GameIds.Normalize(input));
    }

    [Fact]
    public void string重载白名单与枚举重载逐项一致()
    {
        foreach (var game in Enum.GetValues<GameType>())
        {
            var id = GameIds.ToId(game);
            Assert.Equal(GameExecutables.For(game), GameExecutables.For(id));
            foreach (var exe in new[] { "YuanShen.exe", "GenshinImpact.exe", "StarRail.exe", "ZenlessZoneZero.exe", "ZenlessZoneZeroBeta.exe", "notepad.exe" })
            {
                Assert.Equal(GameExecutables.IsAllowed(game, exe), GameExecutables.IsAllowed(id, exe));
            }
        }
    }

    [Theory]
    [InlineData("ext.vendor.game")]
    [InlineData("")]
    [InlineData(null)]
    public void 未知id一律拒绝(string? id)
    {
        Assert.Empty(GameExecutables.For(id));
        Assert.False(GameExecutables.IsAllowed(id, "ZenlessZoneZero.exe"));
        Assert.False(GameProcessDetector.IsGameRunning(id));
    }

    [Fact]
    public void 内置目录包含三款游戏且白名单与GameExecutables一致()
    {
        var catalog = BuiltInGameCatalog.Instance;
        Assert.Equal(GameIds.BuiltIn, catalog.All.Select(item => item.Id));
        foreach (var game in Enum.GetValues<GameType>())
        {
            var descriptor = catalog.Find(GameIds.ToId(game));
            Assert.NotNull(descriptor);
            Assert.Equal(GameExecutables.For(game), descriptor!.ExeNames);
        }

        Assert.Null(catalog.Find("ext.vendor.game"));
        Assert.Null(catalog.Find(null));
    }
}
