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
}
