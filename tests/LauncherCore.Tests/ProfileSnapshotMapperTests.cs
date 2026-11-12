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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 触屏载荷开关映射到快照并经JSON往返保留(bool touchUiEnabled)
    {
        var profile = new GameProfile
        {
            Id = "p1",
            DisplayName = "星铁",
            Game = GameType.StarRail,
            ExecutablePath = @"D:\Games\StarRail.exe",
            Hoyo = new HoyoProfileSettings { TouchUiEnabled = touchUiEnabled },
        };

        var snapshot = ProfileSnapshotMapper.Map(profile);
        Assert.True(snapshot.Hoyo!.HasTouchUiEnabled);
        Assert.Equal(touchUiEnabled, snapshot.Hoyo.TouchUiEnabled);

        // Broker → SessionHost 经环境变量传 protobuf JSON：字段（含显式 false）必须保留。
        var parsed = ProfileJson.Parse(ProfileJson.Format(snapshot));
        Assert.True(parsed.Hoyo.HasTouchUiEnabled);
        Assert.Equal(touchUiEnabled, parsed.Hoyo.TouchUiEnabled);
    }

    [Fact]
    public void 默认Profile触屏载荷开启且旧快照缺省字段保持缺省()
    {
        Assert.True(new HoyoProfileSettings().TouchUiEnabled);

        // 旧客户端的快照没有 touch_ui_enabled：线上缺省，由接收方按「开启」解释。
        var legacy = ProfileJson.Parse("{\"profileId\":\"p\",\"game\":\"starRail\",\"hoyo\":{\"fpsUnlockEnabled\":true,\"targetFps\":120}}");
        Assert.False(legacy.Hoyo.HasTouchUiEnabled);
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
