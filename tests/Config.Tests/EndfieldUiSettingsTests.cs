using System.Globalization;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Config.Tests;

/// <summary>Endfield touch-interface settings: what they turn into in endfield-ui.ini, how they persist in config.json, and that a bad value never costs a profile.</summary>
public sealed class EndfieldUiSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-tests", Guid.NewGuid().ToString("N"));

    public EndfieldUiSettingsTests() => Directory.CreateDirectory(_dir);

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

    // ---- values ----

    [Theory]
    [InlineData("auto", 140, "auto")]
    [InlineData("manual", 140, "1.40")]
    [InlineData("manual", 100, "1.00")]
    [InlineData("manual", 180, "1.80")]
    [InlineData("manual", 0, "1.40")]
    [InlineData("manual", 127, "1.25")]
    [InlineData("manual", 128, "1.30")]
    [InlineData("manual", 99, "1.00")]
    [InlineData("manual", 5000, "1.80")]
    [InlineData("manual", -40, "1.00")]
    [InlineData("MANUAL", 150, "1.50")]
    [InlineData("nonsense", 150, "auto")]
    [InlineData(null, 150, "auto")]
    public void 缩放值按自动或手动写成auto或两位小数(string? mode, int percent, string expected) =>
        Assert.Equal(expected, EndfieldUiSettings.ScaleValue(mode, percent));

    [Theory]
    [InlineData("auto", 10, 20, "auto")]
    [InlineData("off", 10, 20, "off")]
    [InlineData("manual", 0, 0, "0,0")]
    [InlineData("manual", 186, 186, "186,186")]
    [InlineData("manual", -5, 99999, "0,600")]
    [InlineData("whatever", 10, 20, "auto")]
    [InlineData(null, 10, 20, "auto")]
    public void 安全边距按三种方式写成auto_off或左右像素(string? mode, int left, int right, string expected) =>
        Assert.Equal(expected, EndfieldUiSettings.SafeAreaValue(mode, left, right));

    [Fact]
    public void 数值格式与当前区域设置无关()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.40", EndfieldUiSettings.ScaleValue("manual", 140));
            Assert.Equal("12,34", EndfieldUiSettings.SafeAreaValue("manual", 12, 34));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void 没有Endfield设置的Profile两项都是自动()
    {
        Assert.Equal(("auto", "auto"), EndfieldUiSettings.IniValues(null));
        Assert.Equal(("auto", "auto"), EndfieldUiSettings.IniValues(new HoyoProfileSettings()));
    }

    // ---- config.json ----

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private static string Endfield(string id, string hoyo = "") =>
        "{ \"id\": \"" + id + "\", \"displayName\": \"" + id + "\", \"game\": \"endfield\", \"executablePath\": \"D:\\\\Games\\\\Endfield.exe\""
        + (hoyo.Length > 0 ? ", \"hoyo\": { " + hoyo + " }" : "") + " }";

    private void WriteConfig(params string[] profiles) =>
        File.WriteAllText(ConfigPath, $$"""{ "schemaVersion": 1, "selectedProfileId": null, "profiles": [ {{string.Join(",\n", profiles)}} ] }""");

    [Fact]
    public void 没有新字段的旧配置读成默认值且不改动文件()
    {
        WriteConfig(Endfield("old", "\"endfieldInputMode\": \"touch\", \"endfieldAutoResolution\": true, \"endfieldWidth\": 0, \"endfieldHeight\": 0, \"endfieldRefreshRate\": 60"));
        var before = File.ReadAllBytes(ConfigPath);

        var result = new AppConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadState.Loaded, result.State);
        var hoyo = result.Config.Profiles.Single().Hoyo!;
        Assert.Equal(("auto", 140, "auto", 0, 0), (hoyo.EndfieldUiScaleMode, hoyo.EndfieldUiScalePercent, hoyo.EndfieldSafeAreaMode, hoyo.EndfieldSafeLeft, hoyo.EndfieldSafeRight));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Empty(Directory.GetFiles(_dir, "config.*-*.json"));
    }

    [Fact]
    public void 新字段保存后读回相同()
    {
        var store = new AppConfigStore(ConfigPath);
        var config = new AppConfig
        {
            Profiles =
            [
                new GameProfile
                {
                    Id = "e", DisplayName = "e", Game = GameType.Endfield, ExecutablePath = @"D:\Games\Endfield.exe",
                    Hoyo = new HoyoProfileSettings
                    {
                        EndfieldUiScaleMode = "manual", EndfieldUiScalePercent = 145, EndfieldSafeAreaMode = "manual", EndfieldSafeLeft = 70, EndfieldSafeRight = 55,
                    },
                },
            ],
        };
        store.Save(config);

        var back = new AppConfigStore(ConfigPath).Load().Config.Profiles.Single().Hoyo!;

        Assert.Equal(("manual", 145, "manual", 70, 55), (back.EndfieldUiScaleMode, back.EndfieldUiScalePercent, back.EndfieldSafeAreaMode, back.EndfieldSafeLeft, back.EndfieldSafeRight));
    }

    [Fact]
    public void 校验器不把新字段当作拒绝Profile的理由()
    {
        var profile = new GameProfile
        {
            Id = "e", DisplayName = "e", Game = GameType.Endfield, ExecutablePath = @"D:\Games\Endfield.exe",
            Hoyo = new HoyoProfileSettings { EndfieldUiScaleMode = "x", EndfieldUiScalePercent = -1, EndfieldSafeAreaMode = "y", EndfieldSafeLeft = int.MinValue, EndfieldSafeRight = int.MaxValue },
        };

        Assert.Empty(AppConfigValidator.Validate(new AppConfig { Profiles = [profile] }));
    }
}
