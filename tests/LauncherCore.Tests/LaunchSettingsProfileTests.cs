using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class LaunchSettingsProfileTests
{
    private static AppConfig ConfigWithGenshin(out GameProfile profile)
    {
        var config = new AppConfig();
        profile = ProfileManager.Add(config, new ProfileMutation(
            Id: "g", DisplayName: "原神", Game: GameType.Genshin, ExecutablePath: @"D:\Games\YuanShen.exe"));
        return config;
    }

    [Fact]
    public void 新建的配置默认没有启动设置()
    {
        ConfigWithGenshin(out var profile);
        Assert.Null(profile.Launch);
    }

    [Fact]
    public void 更新时整体替换启动设置并规整附加参数()
    {
        var config = ConfigWithGenshin(out _);

        var updated = ProfileManager.Update(config, "g", new ProfileMutation(Launch: new LaunchSettings
        {
            Width = 1920,
            Height = 1080,
            Fullscreen = false,
            Borderless = true,
            ExtraArguments = "  -foo  ",
        }));

        Assert.Equal(1920, updated.Launch!.Width);
        Assert.Equal(false, updated.Launch.Fullscreen);
        Assert.True(updated.Launch.Borderless);
        Assert.Equal("-foo", updated.Launch.ExtraArguments);
    }

    [Fact]
    public void 全部不修改的启动设置会清除这一段()
    {
        var config = ConfigWithGenshin(out _);
        ProfileManager.Update(config, "g", new ProfileMutation(Launch: new LaunchSettings { Width = 1280, Height = 720 }));

        var cleared = ProfileManager.Update(config, "g", new ProfileMutation(Launch: new LaunchSettings { ExtraArguments = "  " }));

        Assert.Null(cleared.Launch);
    }

    [Fact]
    public void 不带Launch的更新保留已有启动设置()
    {
        var config = ConfigWithGenshin(out _);
        ProfileManager.Update(config, "g", new ProfileMutation(Launch: new LaunchSettings { Width = 1280, Height = 720 }));

        var renamed = ProfileManager.Update(config, "g", new ProfileMutation(DisplayName: "原神 国服"));

        Assert.Equal(1280, renamed.Launch!.Width);
    }

    [Fact]
    public void 只给宽不给高会被拒绝且不改动原配置()
    {
        var config = ConfigWithGenshin(out _);

        Assert.Throws<LauncherException>(() =>
            ProfileManager.Update(config, "g", new ProfileMutation(Launch: new LaunchSettings { Width = 1920 })));
        Assert.Null(config.Profiles.Single().Launch);
    }

    [Fact]
    public void 快照映射带上启动设置且不修改的字段映射为零或空串()
    {
        var profile = new GameProfile
        {
            Id = "z",
            DisplayName = "绝区零",
            Game = GameType.ZenlessZoneZero,
            ExecutablePath = @"D:\Z\ZenlessZoneZero.exe",
            Launch = new LaunchSettings { Fullscreen = true, Borderless = true, ExtraArguments = " -x " },
        };

        var launch = ProfileSnapshotMapper.Map(profile).Launch!;

        Assert.Equal(0, launch.Width);
        Assert.Equal(0, launch.Height);
        Assert.Equal(1, launch.Fullscreen);
        Assert.True(launch.Borderless);
        Assert.Equal("-x", launch.ExtraArguments);
    }

    [Fact]
    public void 没有启动设置时快照里没有launch()
    {
        ConfigWithGenshin(out var profile);
        Assert.Null(ProfileSnapshotMapper.Map(profile).Launch);
    }

    [Fact]
    public void 启动设置随config保存并重新读取且未设置时不写出该段()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-launch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new AppConfigStore(path);
            var config = store.Load().Config;
            ProfileManager.Add(config, new ProfileMutation(
                Id: "g", DisplayName: "原神", Game: GameType.Genshin, ExecutablePath: @"D:\Games\YuanShen.exe"));
            store.Save(config);
            Assert.DoesNotContain("\"launch\"", File.ReadAllText(path));

            ProfileManager.Update(config, "g", new ProfileMutation(Launch: new LaunchSettings
            {
                Width = 2560,
                Height = 1440,
                Fullscreen = true,
                Borderless = true,
                ExtraArguments = "-x",
            }));
            store.Save(config);

            var reloaded = new AppConfigStore(path).Load().Config.Profiles.Single().Launch!;
            Assert.Equal(2560, reloaded.Width);
            Assert.Equal(true, reloaded.Fullscreen);
            Assert.True(reloaded.Borderless);
            Assert.Equal("-x", reloaded.ExtraArguments);
            Assert.Contains("\"borderless\": true", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
