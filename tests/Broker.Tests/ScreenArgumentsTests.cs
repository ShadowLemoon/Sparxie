using Sparxie.Broker.Validation;
using Sparxie.Contracts.Rpc;

namespace Broker.Tests;

/// <summary>ProfileSnapshot.screen → Unity arguments: the exact text and the refusals.</summary>
public sealed class ScreenArgumentsTests
{
    private static ProfileSnapshot Profile(ScreenArgs? screen) => new()
    {
        ProfileId = "p1",
        DisplayName = "星铁",
        Game = "starRail",
        ExecutablePath = @"D:\Games\StarRail.exe",
        Hoyo = new HoyoSettings { TargetFps = 120, BackgroundFps = 10, ProcessPriority = "normal", GenshinPreset30Fps = 60, GenshinPreset60Fps = 1000, GenshinTouchUiScalePercent = 400 },
        Screen = screen,
    };

    [Theory]
    [InlineData(1280, 720, "fullscreen", "-screen-width 1280 -screen-height 720 -screen-fullscreen 1")]
    [InlineData(1280, 720, "windowed", "-screen-width 1280 -screen-height 720 -screen-fullscreen 0")]
    [InlineData(1920, 1080, "borderless", "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -popupwindow")]
    [InlineData(2868, 1320, "", "-screen-width 2868 -screen-height 1320")]
    [InlineData(0, 0, "windowed", "-screen-fullscreen 0")]
    public void 参数文本逐字符确定(int width, int height, string mode, string expected)
    {
        Assert.Equal(expected, ScreenArguments.Build(new ScreenArgs { Width = width, Height = height, WindowMode = mode }));
    }

    [Fact]
    public void 没有屏幕参数就没有命令行()
    {
        Assert.Equal(string.Empty, ScreenArguments.Build(null));
        Assert.Equal(string.Empty, ScreenArguments.Build(new ScreenArgs()));
        Assert.Equal(string.Empty, ScreenArguments.CommandLine(@"D:\g\StarRail.exe", null));
    }

    [Fact]
    public void 命令行带引号的可执行文件名()
    {
        Assert.Equal("\"D:\\My Games\\StarRail.exe\" -screen-width 1280 -screen-height 720 -screen-fullscreen 0",
            ScreenArguments.CommandLine(@"D:\My Games\StarRail.exe", new ScreenArgs { Width = 1280, Height = 720, WindowMode = "windowed" }));
    }

    [Theory]
    [InlineData(1280, 0, "")]
    [InlineData(0, 720, "")]
    [InlineData(100, 720, "")]
    [InlineData(1280, 99999, "")]
    [InlineData(-5, -5, "")]
    [InlineData(1280, 720, "exclusive")]
    [InlineData(1280, 720, "-screen-width 5")]
    public void 非法屏幕参数被拒绝且不产生文本(int width, int height, string mode)
    {
        var screen = new ScreenArgs { Width = width, Height = height, WindowMode = mode };
        Assert.NotNull(ScreenArguments.Validate(screen));
        Assert.Equal(string.Empty, ScreenArguments.Build(screen));
        var rejected = ProfileSnapshotValidator.Validate(Profile(screen));
        Assert.NotNull(rejected);
        Assert.Equal(Sparxie.Contracts.Errors.ErrorCode.InvalidArgument, rejected.Value.Code);
    }

    [Fact]
    public void 合法屏幕参数通过Broker校验_缺省也通过()
    {
        Assert.Null(ProfileSnapshotValidator.Validate(Profile(new ScreenArgs { Width = 1280, Height = 720, WindowMode = "windowed" })));
        Assert.Null(ProfileSnapshotValidator.Validate(Profile(null)));
    }

    [Fact]
    public void 屏幕参数经过Host环境变量的JSON往返()
    {
        var back = ProfileJson.Parse(ProfileJson.Format(Profile(new ScreenArgs { Width = 1280, Height = 720, WindowMode = "borderless" })));
        Assert.Equal(1280, back.Screen.Width);
        Assert.Equal(720, back.Screen.Height);
        Assert.Equal("borderless", back.Screen.WindowMode);
        Assert.Null(ProfileJson.Parse(ProfileJson.Format(Profile(null))).Screen);
    }
}
