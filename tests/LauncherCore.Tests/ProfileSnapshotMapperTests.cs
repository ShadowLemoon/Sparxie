using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
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
            Game = GameIds.Genshin,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 触屏关闭开关映射到快照并经JSON往返保留(bool disableTouchUi)
    {
        var profile = new GameProfile
        {
            Id = "p1",
            DisplayName = "星铁",
            Game = GameIds.StarRail,
            ExecutablePath = @"D:\Games\StarRail.exe",
            DisableTouchUi = disableTouchUi,
            Hoyo = new HoyoProfileSettings(),
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);
        Assert.Equal(disableTouchUi, snapshot.DisableTouchUi);

        // Broker → SessionHost 经环境变量传 protobuf JSON：字段必须保留。
        var parsed = ProfileJson.Parse(ProfileJson.Format(snapshot));
        Assert.Equal(disableTouchUi, parsed.DisableTouchUi);
    }

    [Fact]
    public void 默认Profile不关闭触屏且旧快照缺省字段为false()
    {
        var legacy = ProfileJson.Parse("{\"profileId\":\"p\",\"game\":\"starRail\",\"hoyo\":{\"fpsUnlockEnabled\":true,\"targetFps\":120}}");
        Assert.False(legacy.DisableTouchUi);
    }

    [Fact]
    public void 绝区零不生成Hoyo设置()
    {
        var profile = new GameProfile
        {
            Id = "p1",
            DisplayName = "绝区零",
            Game = GameIds.ZenlessZoneZero,
            ExecutablePath = @"D:\Games\ZenlessZoneZero.exe",
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);

        Assert.Equal("zenlessZoneZero", snapshot.Game);
        Assert.Null(snapshot.Hoyo);
        Assert.False(snapshot.DisableTouchUi);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 绝区零触屏开关映射到快照并经JSON往返保留(bool disableTouchUi)
    {
        var profile = new GameProfile
        {
            Id = "zzz-1",
            DisplayName = "绝区零",
            Game = GameIds.ZenlessZoneZero,
            ExecutablePath = @"D:\Games\ZenlessZoneZero.exe",
            DisableTouchUi = disableTouchUi,
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);
        Assert.Null(snapshot.Hoyo);
        Assert.Equal(disableTouchUi, snapshot.DisableTouchUi);

        var parsed = ProfileJson.Parse(ProfileJson.Format(snapshot));
        Assert.Equal(disableTouchUi, parsed.DisableTouchUi);
    }
}
