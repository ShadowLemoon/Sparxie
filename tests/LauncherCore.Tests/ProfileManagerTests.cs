using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class ProfileManagerTests
{
    [Fact]
    public void 创建三种Profile时使用正确默认值并自动选择首项()
    {
        var config = new AppConfig();

        var genshin = ProfileManager.Add(config, Create("genshin-cn", "原神 国服", GameType.Genshin, "cn", @"D:\Games\YuanShen.exe"));
        var zzz = ProfileManager.Add(config, Create("zzz-cn", "绝区零 国服", GameType.ZenlessZoneZero, "cn", @"D:\Games\ZenlessZoneZero.exe"));

        Assert.Equal("genshin-cn", config.SelectedProfileId);
        Assert.Equal(120, genshin.Hoyo!.TargetFps);
        Assert.True(genshin.Hoyo.FpsUnlockEnabled);
        Assert.True(genshin.Hoyo.BackgroundFpsLimitEnabled);
        Assert.Equal(10, genshin.Hoyo.BackgroundFps);
        Assert.Equal(ProcessPriority.Normal, genshin.Hoyo.ProcessPriority);
        Assert.Null(zzz.Hoyo);
    }

    [Fact]
    public void 修改只改变指定字段并保持Profile身份不变()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("genshin-cn", "旧名称", GameType.Genshin, "cn", @"D:\Games\YuanShen.exe"));

        var updated = ProfileManager.Update(config, "genshin-cn", new ProfileMutation(
            DisplayName: "新名称",
            TargetFps: 144,
            ProcessPriority: ProcessPriority.High,
            GenshinTouchUiScaleOverrideEnabled: true,
            GenshinTouchUiScalePercent: 350));

        Assert.Equal("genshin-cn", updated.Id);
        Assert.Equal(GameType.Genshin, updated.Game);
        Assert.Equal("cn", updated.Variant);
        Assert.Equal(@"D:\Games\YuanShen.exe", updated.ExecutablePath);
        Assert.Equal("新名称", updated.DisplayName);
        Assert.Equal(144, updated.Hoyo!.TargetFps);
        Assert.Equal(ProcessPriority.High, updated.Hoyo.ProcessPriority);
        Assert.True(updated.Hoyo.GenshinTouchUiScaleOverrideEnabled);
        Assert.Equal(350, updated.Hoyo.GenshinTouchUiScalePercent);
        Assert.True(updated.Hoyo.BackgroundFpsLimitEnabled);
    }

    [Fact]
    public void 选择和删除默认Profile按约定回落()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("first", "第一项", GameType.StarRail, "cn", @"D:\Games\StarRail.exe"));
        ProfileManager.Add(config, Create("second", "第二项", GameType.StarRail, "intl", @"D:\Games\StarRail.exe"));

        ProfileManager.Select(config, "second");
        ProfileManager.Remove(config, "second");
        Assert.Equal("first", config.SelectedProfileId);

        ProfileManager.Remove(config, "first");
        Assert.Null(config.SelectedProfileId);
        Assert.Empty(config.Profiles);
    }

    [Fact]
    public void 拒绝重复ID不完整路径白名单错误和不适用设置()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("zzz", "绝区零", GameType.ZenlessZoneZero, "cn", @"D:\Games\ZenlessZoneZero.exe"));
        ProfileManager.Add(config, Create("starrail", "星铁", GameType.StarRail, "cn", @"D:\Games\StarRail.exe"));

        Assert.Throws<LauncherException>(() => ProfileManager.Add(
            config,
            Create("zzz", "重复", GameType.ZenlessZoneZero, "intl", @"D:\Games\ZenlessZoneZero.exe")));
        Assert.Throws<LauncherException>(() => ProfileManager.Add(
            config,
            Create("relative", "相对路径", GameType.Genshin, "cn", @"Games\YuanShen.exe")));
        Assert.Throws<LauncherException>(() => ProfileManager.Add(
            config,
            Create("wrong-exe", "错误EXE", GameType.Genshin, "cn", @"D:\Games\StarRail.exe")));
        Assert.Throws<LauncherException>(() => ProfileManager.Update(
            config,
            "zzz",
            new ProfileMutation(TargetFps: 120)));
        Assert.Throws<LauncherException>(() => ProfileManager.Update(
            config,
            "starrail",
            new ProfileMutation(GenshinPreset30Fps: 60)));

        Assert.Null(ProfileSelector.Select(config, "zzz").Hoyo);
    }

    private static ProfileMutation Create(
        string id,
        string name,
        GameType game,
        string variant,
        string executablePath) => new(
            Id: id,
            DisplayName: name,
            Game: game,
            Variant: variant,
            ExecutablePath: executablePath);
}
