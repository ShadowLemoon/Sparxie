using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class ProfileManagerTests
{
    [Fact]
    public void 创建三种Profile时使用正确默认值并自动选择首项()
    {
        var config = new AppConfig();

        var genshin = ProfileManager.Add(config, Create("genshin-cn", "原神 国服", GameType.Genshin, @"D:\Games\YuanShen.exe"));
        var zzz = ProfileManager.Add(config, Create("zzz-cn", "绝区零 国服", GameType.ZenlessZoneZero, @"D:\Games\ZenlessZoneZero.exe"));

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
        ProfileManager.Add(config, Create("genshin-cn", "旧名称", GameType.Genshin, @"D:\Games\YuanShen.exe"));

        var updated = ProfileManager.Update(config, "genshin-cn", new ProfileMutation(
            DisplayName: "新名称",
            TargetFps: 144,
            ProcessPriority: ProcessPriority.High,
            GenshinTouchUiScaleOverrideEnabled: true,
            GenshinTouchUiScalePercent: 350));

        Assert.Equal("genshin-cn", updated.Id);
        Assert.Equal(GameType.Genshin, updated.Game);
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
        ProfileManager.Add(config, Create("first", "第一项", GameType.StarRail, @"D:\Games\StarRail.exe"));
        ProfileManager.Add(config, Create("second", "第二项", GameType.StarRail, @"D:\Games\StarRail.exe"));

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
        ProfileManager.Add(config, Create("zzz", "绝区零", GameType.ZenlessZoneZero, @"D:\Games\ZenlessZoneZero.exe"));
        ProfileManager.Add(config, Create("starrail", "星铁", GameType.StarRail, @"D:\Games\StarRail.exe"));

        Assert.Throws<LauncherException>(() => ProfileManager.Add(
            config,
            Create("zzz", "重复", GameType.ZenlessZoneZero, @"D:\Games\ZenlessZoneZero.exe")));
        Assert.Throws<LauncherException>(() => ProfileManager.Add(
            config,
            Create("relative", "相对路径", GameType.Genshin, @"Games\YuanShen.exe")));
        Assert.Throws<LauncherException>(() => ProfileManager.Add(
            config,
            Create("wrong-exe", "错误EXE", GameType.Genshin, @"D:\Games\StarRail.exe")));
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

    [Fact]
    public void 允许创建空路径占位Profile并保留未完成状态()
    {
        var config = new AppConfig();

        var placeholder = ProfileManager.Add(config, new ProfileMutation(
            Id: "default",
            DisplayName: "默认配置",
            Game: GameType.Genshin));

        Assert.Equal(string.Empty, placeholder.ExecutablePath);
        Assert.Equal("default", config.SelectedProfileId);
        Assert.NotNull(placeholder.Hoyo);
    }

    [Fact]
    public void 修改时传null或空白路径都不改动原路径()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("genshin-cn", "原神", GameType.Genshin, @"D:\Games\YuanShen.exe"));

        var byNull = ProfileManager.Update(config, "genshin-cn", new ProfileMutation(TargetFps: 144));
        Assert.Equal(@"D:\Games\YuanShen.exe", byNull.ExecutablePath);

        // 界面上「路径框留空」不代表要把路径清掉；空白输入伴随其他改动时仍保留原路径。
        var byBlank = ProfileManager.Update(config, "genshin-cn", new ProfileMutation(
            DisplayName: "改名但没填路径",
            ExecutablePath: "   "));
        Assert.Equal(@"D:\Games\YuanShen.exe", byBlank.ExecutablePath);
        Assert.Equal("改名但没填路径", byBlank.DisplayName);
    }

    [Fact]
    public void 占位Profile可继续修改其他字段而不因空路径失败()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, new ProfileMutation(
            Id: "default",
            DisplayName: "默认配置",
            Game: GameType.Genshin));

        var updated = ProfileManager.Update(config, "default", new ProfileMutation(
            DisplayName: "改个名字",
            TargetFps: 165));

        Assert.Equal("改个名字", updated.DisplayName);
        Assert.Equal(165, updated.Hoyo!.TargetFps);
        Assert.Equal(string.Empty, updated.ExecutablePath);
    }

    [Fact]
    public void 修改游戏类型会生效并清空不再匹配的旧路径()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("main", "主配置", GameType.Genshin, @"D:\Games\YuanShen.exe"));

        var toStarRail = ProfileManager.Update(config, "main", new ProfileMutation(Game: GameType.StarRail));

        Assert.Equal(GameType.StarRail, toStarRail.Game);
        Assert.Equal(string.Empty, toStarRail.ExecutablePath);
        Assert.Equal(GameType.StarRail, ProfileSelector.Select(config, "main").Game);
    }

    [Fact]
    public void 改成绝区零会移除Hoyo设置改回则重新补齐()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("main", "主配置", GameType.Genshin, @"D:\Games\YuanShen.exe"));

        var toZzz = ProfileManager.Update(config, "main", new ProfileMutation(Game: GameType.ZenlessZoneZero));
        Assert.Null(toZzz.Hoyo);

        var backToGenshin = ProfileManager.Update(config, "main", new ProfileMutation(Game: GameType.Genshin));
        Assert.NotNull(backToGenshin.Hoyo);
        Assert.Equal(120, backToGenshin.Hoyo!.TargetFps);
    }

    [Fact]
    public void 同时修改游戏类型和路径时按新游戏白名单校验()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("main", "主配置", GameType.Genshin, @"D:\Games\YuanShen.exe"));

        var updated = ProfileManager.Update(config, "main", new ProfileMutation(
            Game: GameType.StarRail,
            ExecutablePath: @"D:\Games\StarRail.exe"));
        Assert.Equal(@"D:\Games\StarRail.exe", updated.ExecutablePath);

        Assert.Throws<LauncherException>(() => ProfileManager.Update(config, "main", new ProfileMutation(
            Game: GameType.Genshin,
            ExecutablePath: @"D:\Games\StarRail.exe")));
    }

    [Fact]
    public void 绝区零占位Profile仅提交名称和游戏也能保存()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, new ProfileMutation(
            Id: "zzz",
            DisplayName: "绝区零",
            Game: GameType.ZenlessZoneZero));

        // 绝区零没有任何 Hoyo 设置可提交，界面保存时只带名称与游戏类型，不应被当成「无可修改字段」。
        var updated = ProfileManager.Update(config, "zzz", new ProfileMutation(
            DisplayName: "绝区零",
            Game: GameType.ZenlessZoneZero));

        Assert.Equal("绝区零", updated.DisplayName);
        Assert.Equal(GameType.ZenlessZoneZero, updated.Game);
        Assert.Null(updated.Hoyo);
        Assert.Equal(string.Empty, updated.ExecutablePath);
    }

    [Fact]
    public void 终末地设置可修改且仅限终末地()
    {
        var config = new AppConfig();
        ProfileManager.Add(config, Create("endfield-cn", "终末地", GameType.Endfield, @"D:\Games\Endfield.exe"));

        var updated = ProfileManager.Update(config, "endfield-cn", new ProfileMutation(
            EndfieldInputMode: "pc",
            EndfieldAutoResolution: false,
            EndfieldWidth: 2560,
            EndfieldHeight: 1440,
            EndfieldRefreshRate: 144));

        Assert.Equal("pc", updated.Hoyo!.EndfieldInputMode);
        Assert.False(updated.Hoyo.EndfieldAutoResolution);
        Assert.Equal((2560, 1440, 144), (updated.Hoyo.EndfieldWidth, updated.Hoyo.EndfieldHeight, updated.Hoyo.EndfieldRefreshRate));
    }

    private static ProfileMutation Create(
        string id,
        string name,
        GameType game,
        string executablePath) => new(
            Id: id,
            DisplayName: name,
            Game: game,
            ExecutablePath: executablePath);
}
