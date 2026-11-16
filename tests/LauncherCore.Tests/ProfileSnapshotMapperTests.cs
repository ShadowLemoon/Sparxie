using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class ProfileSnapshotMapperTests
{
    [Fact]
    public void 映射完整Profile快照()
    {
        var profile = new GameProfile
        {
            Id = "p1",
            DisplayName = "原神",
            Game = GameType.Genshin,
            ExecutablePath = @"D:\Games\GenshinImpact.exe",
            Hoyo = new HoyoProfileSettings
            {
                FpsUnlockEnabled = false,
                TargetFps = 144,
                BackgroundFpsLimitEnabled = false,
                BackgroundFps = 30,
                ProcessPriority = ProcessPriority.High,
                GenshinFollowInGamePreset = true,
                GenshinPreset30Fps = 60,
                GenshinPreset60Fps = 240,
                GenshinTouchUiScaleOverrideEnabled = true,
                GenshinTouchUiScalePercent = 350,
            },
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);

        Assert.Equal("p1", snapshot.ProfileId);
        Assert.Equal("genshin", snapshot.Game);
        Assert.Equal(144, snapshot.Hoyo!.TargetFps);
        Assert.Equal("high", snapshot.Hoyo.ProcessPriority);
        Assert.True(snapshot.Hoyo.GenshinFollowInGamePreset);
        Assert.Equal(350, snapshot.Hoyo.GenshinTouchUiScalePercent);
    }

    [Fact]
    public void 绝区零不生成Hoyo设置()
    {
        var profile = new GameProfile
        {
            Id = "p1",
            DisplayName = "绝区零",
            Game = GameType.ZenlessZoneZero,
            ExecutablePath = @"D:\Games\ZenlessZoneZero.exe",
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);

        Assert.Equal("zenlessZoneZero", snapshot.Game);
        Assert.Null(snapshot.Hoyo);
    }

    [Fact]
    public void 终末地Profile映射游戏标识和专属设置()
    {
        var profile = new GameProfile
        {
            Id = "e1",
            DisplayName = "终末地",
            Game = GameType.Endfield,
            ExecutablePath = @"D:\Games\Endfield.exe",
            Hoyo = new HoyoProfileSettings
            {
                EndfieldInputMode = "pc",
                EndfieldAutoResolution = false,
                EndfieldWidth = 2560,
                EndfieldHeight = 1440,
                EndfieldRefreshRate = 144,
            },
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);

        Assert.Equal("endfield", snapshot.Game);
        Assert.Equal("pc", snapshot.Hoyo!.EndfieldInputMode);
        Assert.False(snapshot.Hoyo.EndfieldAutoResolution);
        Assert.Equal((2560, 1440, 144), (snapshot.Hoyo.EndfieldWidth, snapshot.Hoyo.EndfieldHeight, snapshot.Hoyo.EndfieldRefreshRate));
    }
}
