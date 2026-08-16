using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class ProfileSelectorTests
{
    [Fact]
    public void 缺省选择优先使用SelectedProfileId()
    {
        var first = Profile("first", "第一个");
        var second = Profile("second", "第二个");
        var config = new AppConfig
        {
            SelectedProfileId = "second",
            Profiles = [first, second],
        };

        Assert.Same(second, ProfileSelector.Select(config));
    }

    [Fact]
    public void 可按ID或名称选择()
    {
        var profile = Profile("p1", "星铁");
        var config = new AppConfig { Profiles = [profile] };

        Assert.Same(profile, ProfileSelector.Select(config, "p1"));
        Assert.Same(profile, ProfileSelector.Select(config, "星铁"));
    }

    [Fact]
    public void 重名和不存在Profile被拒绝()
    {
        var config = new AppConfig
        {
            Profiles = [Profile("p1", "重复"), Profile("p2", "重复")],
        };

        var duplicate = Assert.Throws<LauncherException>(() => ProfileSelector.Select(config, "重复"));
        Assert.Equal(LauncherFailureKind.ProfileSelection, duplicate.Kind);

        var missing = Assert.Throws<LauncherException>(() => ProfileSelector.Select(config, "missing"));
        Assert.Contains("找不到", missing.Message);
    }

    private static GameProfile Profile(string id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        Game = GameType.StarRail,
        Variant = "cn",
        ExecutablePath = @"D:\Games\StarRail.exe",
        Hoyo = new HoyoProfileSettings(),
    };
}
