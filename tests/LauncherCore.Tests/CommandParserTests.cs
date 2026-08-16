using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class CommandParserTests
{
    [Fact]
    public void 解析帮助列表启动命令()
    {
        Assert.Equal(LauncherCommandKind.Help, LauncherCommandParser.Parse([]).Command!.Kind);
        Assert.Equal(LauncherCommandKind.List, LauncherCommandParser.Parse(["list"]).Command!.Kind);
        var launch = LauncherCommandParser.Parse(["launch", "p1"]);
        Assert.Equal(LauncherCommandKind.Launch, launch.Command!.Kind);
        Assert.Equal("p1", launch.Command.ProfileSelector);
    }

    [Fact]
    public void 拒绝未知和多余参数()
    {
        Assert.False(LauncherCommandParser.Parse(["unknown"]).Success);
        Assert.False(LauncherCommandParser.Parse(["list", "extra"]).Success);
        Assert.False(LauncherCommandParser.Parse(["launch", "a", "b"]).Success);
    }

    [Fact]
    public void 解析交互热调和退出()
    {
        Assert.True(LauncherInputParser.TryParse("fps 120", out var fps, out _));
        Assert.Equal(LauncherInputCommandKind.SetTargetFps, fps.Kind);
        Assert.Equal(120, fps.TargetFps);

        Assert.True(LauncherInputParser.TryParse("quit", out var quit, out _));
        Assert.Equal(LauncherInputCommandKind.Quit, quit.Kind);

        Assert.False(LauncherInputParser.TryParse("fps 9", out _, out var error));
        Assert.Contains("10", error);
    }
}
