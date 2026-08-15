using Sparxie.Broker.Validation;
using Sparxie.Contracts.Rpc;

namespace Broker.Tests;

public class ProfileSnapshotValidatorTests
{
    private static ProfileSnapshot Valid() => new()
    {
        ProfileId = "p1",
        DisplayName = "原神",
        Game = "genshin",
        Variant = "cn",
        ExecutablePath = @"D:\Games\YuanShen.exe",
        Hoyo = new HoyoSettings
        {
            TargetFps = 120,
            BackgroundFps = 10,
            ProcessPriority = "normal",
            GenshinPreset30Fps = 60,
            GenshinPreset60Fps = 1000,
            GenshinTouchUiScalePercent = 400,
        },
    };

    [Fact]
    public void 合法快照通过()
    {
        Assert.Null(ProfileSnapshotValidator.Validate(Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wow")]
    [InlineData("Genshin")]
    public void 非法game被拒绝(string game)
    {
        var profile = Valid();
        profile.Game = game;
        Assert.NotNull(ProfileSnapshotValidator.Validate(profile));
    }

    [Fact]
    public void 空executablePath被拒绝()
    {
        var profile = Valid();
        profile.ExecutablePath = "";
        Assert.NotNull(ProfileSnapshotValidator.Validate(profile));
    }

    [Fact]
    public void null快照被拒绝()
    {
        Assert.NotNull(ProfileSnapshotValidator.Validate(null!));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1001)]
    public void targetFps越界被拒绝(int fps)
    {
        var profile = Valid();
        profile.Hoyo!.TargetFps = fps;
        Assert.NotNull(ProfileSnapshotValidator.Validate(profile));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(501)]
    public void 缩放百分比越界被拒绝(int percent)
    {
        var profile = Valid();
        profile.Hoyo!.GenshinTouchUiScalePercent = percent;
        Assert.NotNull(ProfileSnapshotValidator.Validate(profile));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("belowNormal")]
    [InlineData("aboveNormal")]
    [InlineData("high")]
    public void 四档优先级通过(string priority)
    {
        var profile = Valid();
        profile.Hoyo!.ProcessPriority = priority;
        Assert.Null(ProfileSnapshotValidator.Validate(profile));
    }

    [Fact]
    public void realtime优先级被拒绝()
    {
        var profile = Valid();
        profile.Hoyo!.ProcessPriority = "realtime";
        Assert.NotNull(ProfileSnapshotValidator.Validate(profile));
    }
}
