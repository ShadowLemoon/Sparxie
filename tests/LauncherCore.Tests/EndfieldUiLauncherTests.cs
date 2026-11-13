using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

/// <summary>The Endfield touch-interface settings through the profile manager and into the snapshot the Broker receives.</summary>
public sealed class EndfieldUiLauncherTests
{
    private static GameProfile Endfield(HoyoProfileSettings? hoyo = null) => new()
    {
        Id = "e1", DisplayName = "终末地", Game = GameType.Endfield, ExecutablePath = @"D:\Games\Endfield.exe", Hoyo = hoyo,
    };

    [Fact]
    public void 新建的终末地配置默认缩放自动安全边距自动()
    {
        var created = ProfileManager.Add(new AppConfig(), new ProfileMutation(Id: "e1", DisplayName: "终末地", Game: GameType.Endfield, ExecutablePath: @"D:\Games\Endfield.exe"));

        var snapshot = ProfileSnapshotMapper.Map(created);

        Assert.Equal(("auto", 125, "auto", 0, 0), (snapshot.Hoyo!.EndfieldUiScaleMode, snapshot.Hoyo.EndfieldUiScalePercent, snapshot.Hoyo.EndfieldSafeAreaMode, snapshot.Hoyo.EndfieldSafeLeft, snapshot.Hoyo.EndfieldSafeRight));
    }

    [Fact]
    public void 修改写入Profile并映射到快照()
    {
        var config = new AppConfig { Profiles = [Endfield(new HoyoProfileSettings())] };

        var updated = ProfileManager.Update(config, "e1", new ProfileMutation(
            EndfieldUiScaleMode: "manual", EndfieldUiScalePercent: 150, EndfieldSafeAreaMode: "manual", EndfieldSafeLeft: 80, EndfieldSafeRight: 70));
        var hoyo = ProfileSnapshotMapper.Map(updated).Hoyo!;

        Assert.Equal(("manual", 150, "manual", 80, 70), (hoyo.EndfieldUiScaleMode, hoyo.EndfieldUiScalePercent, hoyo.EndfieldSafeAreaMode, hoyo.EndfieldSafeLeft, hoyo.EndfieldSafeRight));
    }

    [Fact]
    public void 超出范围的值被收进范围而不是让保存失败()
    {
        var config = new AppConfig { Profiles = [Endfield(new HoyoProfileSettings())] };

        var updated = ProfileManager.Update(config, "e1", new ProfileMutation(
            EndfieldUiScaleMode: "weird", EndfieldUiScalePercent: 900, EndfieldSafeAreaMode: "weird", EndfieldSafeLeft: -9, EndfieldSafeRight: 70000));

        Assert.Equal(("auto", 180, "auto", 0, 600), (updated.Hoyo!.EndfieldUiScaleMode, updated.Hoyo.EndfieldUiScalePercent, updated.Hoyo.EndfieldSafeAreaMode, updated.Hoyo.EndfieldSafeLeft, updated.Hoyo.EndfieldSafeRight));
    }

    [Fact]
    public void 只改一项时其余项不变()
    {
        var config = new AppConfig
        {
            Profiles = [Endfield(new HoyoProfileSettings { EndfieldUiScaleMode = "manual", EndfieldUiScalePercent = 160, EndfieldSafeAreaMode = "off" })],
        };

        var updated = ProfileManager.Update(config, "e1", new ProfileMutation(EndfieldSafeAreaMode: "auto"));

        Assert.Equal(("manual", 160, "auto"), (updated.Hoyo!.EndfieldUiScaleMode, updated.Hoyo.EndfieldUiScalePercent, updated.Hoyo.EndfieldSafeAreaMode));
    }

    [Fact]
    public void 快照经JSON往返后保留新字段()
    {
        var snapshot = ProfileSnapshotMapper.Map(Endfield(new HoyoProfileSettings
        {
            EndfieldUiScaleMode = "manual", EndfieldUiScalePercent = 135, EndfieldSafeAreaMode = "manual", EndfieldSafeLeft = 12, EndfieldSafeRight = 34,
        }));

        var parsed = ProfileJson.Parse(ProfileJson.Format(snapshot)).Hoyo;

        Assert.Equal(("manual", 135, "manual", 12, 34), (parsed.EndfieldUiScaleMode, parsed.EndfieldUiScalePercent, parsed.EndfieldSafeAreaMode, parsed.EndfieldSafeLeft, parsed.EndfieldSafeRight));
    }

    [Fact]
    public void 旧快照没有新字段时读成空值由接收方按默认处理()
    {
        var legacy = ProfileJson.Parse("{\"profileId\":\"p\",\"game\":\"endfield\",\"hoyo\":{\"endfieldInputMode\":\"touch\"}}").Hoyo;

        Assert.Equal(("", 0, "", 0, 0), (legacy.EndfieldUiScaleMode, legacy.EndfieldUiScalePercent, legacy.EndfieldSafeAreaMode, legacy.EndfieldSafeLeft, legacy.EndfieldSafeRight));
    }
}
