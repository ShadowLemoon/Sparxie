using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

public class LaunchArgumentsTests
{
    [Fact]
    public void 无边框自动注入popupwindow()
    {
        Assert.Equal("-popupwindow", LaunchArguments.Build(true, null));
    }

    [Fact]
    public void 无边框参数在前附加参数在后()
    {
        Assert.Equal("-popupwindow -foo 1", LaunchArguments.Build(true, "  -foo 1 "));
    }

    [Fact]
    public void 用户已写popupwindow时不重复注入()
    {
        Assert.Equal("-POPUPWINDOW -x", LaunchArguments.Build(true, "-POPUPWINDOW -x"));
    }

    [Fact]
    public void 不勾选无边框时只有附加参数()
    {
        Assert.Equal(string.Empty, LaunchArguments.Build(false, null));
        Assert.Equal("-x", LaunchArguments.Build(false, "-x"));
    }

    [Fact]
    public void 无边框与全屏互相独立()
    {
        // 同 Starward：无边框只决定是否加 -popupwindow，不看全屏。
        Assert.Equal("-popupwindow", LaunchArguments.Build(new LaunchSettings { Borderless = true, Fullscreen = true }));
        Assert.Equal("-popupwindow", LaunchArguments.Build(new LaunchSettings { Borderless = true, Fullscreen = false }));
        Assert.Equal(string.Empty, LaunchArguments.Build(new LaunchSettings { Fullscreen = true }));
    }
}

public class LaunchSettingsRulesTests
{
    [Fact]
    public void 宽高必须同时设置或同时缺省()
    {
        Assert.NotNull(LaunchSettingsRules.Validate(1920, null, null));
        Assert.NotNull(LaunchSettingsRules.Validate(null, 1080, null));
        Assert.Null(LaunchSettingsRules.Validate(null, null, null));
        Assert.Null(LaunchSettingsRules.Validate(1920, 1080, null));
    }

    [Theory]
    [InlineData(639, 1080)]
    [InlineData(1920, 479)]
    [InlineData(16385, 1080)]
    public void 分辨率超出范围被拒绝(int width, int height)
    {
        Assert.NotNull(LaunchSettingsRules.Validate(width, height, null));
    }

    [Fact]
    public void 常用的最小分辨率640乘480合法()
    {
        Assert.Null(LaunchSettingsRules.Validate(640, 480, null));
    }

    [Fact]
    public void 附加参数过长或含控制字符被拒绝()
    {
        Assert.NotNull(LaunchSettingsRules.Validate(null, null, new string('a', LaunchSettingsRules.MaxExtraArgumentsLength + 1)));
        Assert.NotNull(LaunchSettingsRules.Validate(null, null, "-a\r\n-b"));
        Assert.Null(LaunchSettingsRules.Validate(null, null, "-a -b \"c d\""));
    }

    [Fact]
    public void 配置校验会拒绝不合法的启动设置()
    {
        var config = new AppConfig
        {
            Profiles =
            [
                new GameProfile
                {
                    Id = "p",
                    DisplayName = "原神",
                    Game = GameType.Genshin,
                    ExecutablePath = @"D:\Games\YuanShen.exe",
                    Hoyo = new HoyoProfileSettings(),
                    Launch = new LaunchSettings { Width = 1920 },
                },
            ],
        };

        Assert.Contains(AppConfigValidator.Validate(config), e => e.Contains("启动设置"));
    }

    [Fact]
    public void 只勾选无边框也算有启动设置()
    {
        Assert.False(new LaunchSettings { Borderless = true }.IsEmpty);
        Assert.True(new LaunchSettings().IsEmpty);
    }
}

public class GameRegistrySettingsTests
{
    private sealed class FakeRegistry : IGameRegistry
    {
        public Dictionary<string, Dictionary<string, object>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry(params string[] existing)
        {
            foreach (var key in existing)
            {
                Keys[key] = new Dictionary<string, object>();
            }
        }

        public bool KeyExists(string subKey) => Keys.ContainsKey(subKey);

        public int? GetDword(string subKey, string name) =>
            Keys.TryGetValue(subKey, out var values) && values.TryGetValue(name, out var v) && v is int i ? i : null;

        public byte[]? GetBytes(string subKey, string name) =>
            Keys.TryGetValue(subKey, out var values) && values.TryGetValue(name, out var v) ? v as byte[] : null;

        public IReadOnlyList<string> GetSubKeyNames(string subKey)
        {
            var prefix = subKey + "\\";
            // 真实注册表里中间层级的键一定存在：取所有以 prefix 开头的键的下一段名字。
            return Keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string? GetString(string subKey, string name) =>
            Keys.TryGetValue(subKey, out var values) && values.TryGetValue(name, out var v) ? v as string : null;

        public void SetString(string subKey, string name, string value)
        {
            if (!Keys.ContainsKey(subKey))
            {
                Keys[subKey] = new Dictionary<string, object>();
            }

            Keys[subKey][name] = value;
        }

        public void SetDword(string subKey, string name, int value) => Keys[subKey][name] = value;

        public void SetBytes(string subKey, string name, byte[] value) => Keys[subKey][name] = value;
    }

    private const string GenshinCn = @"Software\miHoYo\原神";
    private const string StarRailCn = @"Software\miHoYo\崩坏：星穹铁道";
    private const string StarRailGlobal = @"Software\Cognosphere\Star Rail";
    private const string ZzzCn = @"Software\miHoYo\绝区零";

    [Fact]
    public void 原神按EXE选键并写分辨率与是否全屏()
    {
        var registry = new FakeRegistry(GenshinCn);
        var result = GameRegistrySettings.Apply(
            GameType.Genshin, @"D:\Games\YuanShen.exe",
            new LaunchSettings { Width = 1920, Height = 1080, Fullscreen = true }, registry);

        Assert.True(result.Applied);
        Assert.Equal(1920, registry.GetDword(GenshinCn, GameRegistrySettings.ResolutionWidth));
        Assert.Equal(1080, registry.GetDword(GenshinCn, GameRegistrySettings.ResolutionHeight));
        Assert.Equal(1, registry.GetDword(GenshinCn, GameRegistrySettings.IsFullscreen));
    }

    [Fact]
    public void 原神国际服EXE只写国际服键()
    {
        var global = @"Software\miHoYo\Genshin Impact";
        var registry = new FakeRegistry(GenshinCn, global);
        GameRegistrySettings.Apply(GameType.Genshin, @"D:\G\GenshinImpact.exe",
            new LaunchSettings { Fullscreen = false }, registry);

        Assert.Equal(0, registry.GetDword(global, GameRegistrySettings.IsFullscreen));
        Assert.Null(registry.GetDword(GenshinCn, GameRegistrySettings.IsFullscreen));
    }

    [Fact]
    public void 绝区零全屏写1窗口写3且只写分辨率三项()
    {
        var registry = new FakeRegistry(ZzzCn);
        GameRegistrySettings.Apply(GameType.ZenlessZoneZero, @"D:\Z\ZenlessZoneZero.exe",
            new LaunchSettings { Width = 2560, Height = 1440, Fullscreen = true }, registry);

        Assert.Equal(1, registry.GetDword(ZzzCn, GameRegistrySettings.FullscreenMode));
        Assert.Equal(2560, registry.GetDword(ZzzCn, GameRegistrySettings.ResolutionWidth));
        Assert.Equal(3, registry.Keys[ZzzCn].Count);

        GameRegistrySettings.Apply(GameType.ZenlessZoneZero, @"D:\Z\ZenlessZoneZero.exe",
            new LaunchSettings { Fullscreen = false }, registry);
        Assert.Equal(3, registry.GetDword(ZzzCn, GameRegistrySettings.FullscreenMode));
    }

    [Fact]
    public void 星铁国服国际服同名EXE有几个键就都写并写JSON()
    {
        var registry = new FakeRegistry(StarRailCn, StarRailGlobal);
        var result = GameRegistrySettings.Apply(GameType.StarRail, @"D:\S\StarRail.exe",
            new LaunchSettings { Width = 1600, Height = 900, Fullscreen = false }, registry);

        Assert.Equal(2, result.WrittenKeys.Count);
        foreach (var key in new[] { StarRailCn, StarRailGlobal })
        {
            Assert.Equal(3, registry.GetDword(key, GameRegistrySettings.FullscreenMode));
            var json = System.Text.Encoding.UTF8.GetString(registry.GetBytes(key, GameRegistrySettings.StarRailResolution)!);
            Assert.EndsWith("\0", json);
            Assert.Contains("\"width\":1600", json);
            Assert.Contains("\"height\":900", json);
            Assert.Contains("\"isFullScreen\":false", json);
        }
    }

    [Fact]
    public void 星铁只改全屏时沿用已有JSON的宽高()
    {
        var registry = new FakeRegistry(StarRailCn);
        registry.SetBytes(StarRailCn, GameRegistrySettings.StarRailResolution,
            System.Text.Encoding.UTF8.GetBytes("{\"width\":1280,\"height\":720,\"isFullScreen\":false}\0"));

        GameRegistrySettings.Apply(GameType.StarRail, @"D:\S\StarRail.exe",
            new LaunchSettings { Fullscreen = true }, registry);

        var json = System.Text.Encoding.UTF8.GetString(registry.GetBytes(StarRailCn, GameRegistrySettings.StarRailResolution)!);
        Assert.Contains("\"width\":1280", json);
        Assert.Contains("\"isFullScreen\":true", json);
    }

    [Fact]
    public void 星铁只改全屏且没有已有JSON时不写JSON()
    {
        var registry = new FakeRegistry(StarRailCn);
        GameRegistrySettings.Apply(GameType.StarRail, @"D:\S\StarRail.exe",
            new LaunchSettings { Fullscreen = true }, registry);

        Assert.Null(registry.GetBytes(StarRailCn, GameRegistrySettings.StarRailResolution));
        Assert.Equal(1, registry.GetDword(StarRailCn, GameRegistrySettings.FullscreenMode));
    }

    [Fact]
    public void 游戏键不存在时不创建并给出说明()
    {
        var registry = new FakeRegistry();
        var result = GameRegistrySettings.Apply(GameType.Genshin, @"D:\G\YuanShen.exe",
            new LaunchSettings { Width = 1920, Height = 1080 }, registry);

        Assert.False(result.Applied);
        Assert.NotNull(result.Note);
        Assert.Empty(registry.Keys);
    }

    [Fact]
    public void 只有无边框或附加参数时不碰注册表()
    {
        var registry = new FakeRegistry(GenshinCn);
        var result = GameRegistrySettings.Apply(GameType.Genshin, @"D:\G\YuanShen.exe",
            new LaunchSettings { Borderless = true, ExtraArguments = "-x" }, registry);

        Assert.False(result.Applied);
        Assert.Null(result.Note);
        Assert.Empty(registry.Keys[GenshinCn]);
    }

    [Fact]
    public void 真实注册表实现在临时键下读写往返()
    {
        var root = $@"Software\SparxieTests\{Guid.NewGuid():N}";
        var registry = new CurrentUserGameRegistry(root);
        try
        {
            registry.SetDword("game", "w", 1920);
            registry.SetBytes("game", "j", [1, 2, 3]);

            Assert.True(registry.KeyExists("game"));
            Assert.False(registry.KeyExists("other"));
            Assert.Equal(1920, registry.GetDword("game", "w"));
            Assert.Equal(new byte[] { 1, 2, 3 }, registry.GetBytes("game", "j"));
            Assert.Null(registry.GetDword("game", "missing"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SparxieTests", throwOnMissingSubKey: false);
        }
    }

    private const string ZzzGlobal = @"Software\miHoYo\ZenlessZoneZero";

    // 与真实注册表一致：…\HYP\standalone\1_5\nap_global\<随机>\nap_global 下的 GameInstallPath
    private static void RegisterHyp(FakeRegistry registry, string key, string installPath) =>
        registry.SetString(key, "GameInstallPath", installPath);

    [Fact]
    public void 启动器登记了国际服安装路径时只写国际服键()
    {
        var registry = new FakeRegistry(ZzzCn, ZzzGlobal);
        RegisterHyp(registry, @"Software\Cognosphere\HYP\standalone\1_5\nap_global\Abc\nap_global",
            "D:/Game/Steam/steamapps/common/Zenless Zone Zero/games/ZenlessZoneZero Game");

        var result = GameRegistrySettings.Apply(GameType.ZenlessZoneZero,
            @"D:\Game\Steam\steamapps\common\Zenless Zone Zero\games\ZenlessZoneZero Game\ZenlessZoneZero.exe",
            new LaunchSettings { Fullscreen = false }, registry);

        Assert.Equal([ZzzGlobal], result.WrittenKeys);
        Assert.Equal(3, registry.GetDword(ZzzGlobal, GameRegistrySettings.FullscreenMode));
        Assert.Null(registry.GetDword(ZzzCn, GameRegistrySettings.FullscreenMode));
    }

    [Fact]
    public void 启动器登记了国服安装路径时只写国服键()
    {
        var registry = new FakeRegistry(StarRailCn, StarRailGlobal);
        RegisterHyp(registry, @"Software\miHoYo\HYP\1_1\hkrpg_cn", @"E:\Games\StarRail Game");

        var result = GameRegistrySettings.Apply(GameType.StarRail, @"E:\Games\StarRail Game\StarRail.exe",
            new LaunchSettings { Width = 1280, Height = 720 }, registry);

        Assert.Equal([StarRailCn], result.WrittenKeys);
    }

    [Fact]
    public void 登记路径与EXE对不上时回到有几个键写几个()
    {
        var registry = new FakeRegistry(StarRailCn, StarRailGlobal);
        RegisterHyp(registry, @"Software\miHoYo\HYP\1_1\hkrpg_cn", @"E:\Other\Place");

        var result = GameRegistrySettings.Apply(GameType.StarRail, @"E:\Games\StarRail Game\StarRail.exe",
            new LaunchSettings { Fullscreen = true }, registry);

        Assert.Equal(2, result.WrittenKeys.Count);
    }

    [Fact]
    public void 登记路径只匹配同一游戏的服务器标识()
    {
        // 星铁的安装路径登记在 hkrpg_cn 下，不能被当成绝区零的国服。
        var registry = new FakeRegistry(ZzzCn, ZzzGlobal);
        RegisterHyp(registry, @"Software\miHoYo\HYP\1_1\hkrpg_cn", @"E:\Games\Shared");

        Assert.Equal(GameServer.Unknown,
            InstalledServerLocator.Resolve(GameType.ZenlessZoneZero, @"E:\Games\Shared\ZenlessZoneZero.exe", registry));
    }

    [Fact]
    public void 路径前缀必须在目录边界上匹配()
    {
        var registry = new FakeRegistry();
        RegisterHyp(registry, @"Software\miHoYo\HYP\1_1\nap_cn", @"E:\Games\ZZZ");

        Assert.Equal(GameServer.Unknown,
            InstalledServerLocator.Resolve(GameType.ZenlessZoneZero, @"E:\Games\ZZZ2\ZenlessZoneZero.exe", registry));
        Assert.Equal(GameServer.Cn,
            InstalledServerLocator.Resolve(GameType.ZenlessZoneZero, @"E:\Games\ZZZ\ZenlessZoneZero.exe", registry));
    }
}
