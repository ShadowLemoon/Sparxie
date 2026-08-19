using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class CommandParserTests
{
    [Fact]
    public void 解析既有帮助列表和启动命令()
    {
        Assert.Equal(LauncherCommandKind.Help, LauncherCommandParser.Parse([]).Command!.Kind);
        Assert.Equal(LauncherCommandKind.List, LauncherCommandParser.Parse(["list"]).Command!.Kind);
        Assert.Equal(LauncherCommandKind.List, LauncherCommandParser.Parse(["profile", "list"]).Command!.Kind);

        var launch = LauncherCommandParser.Parse(["launch", "p1"]);
        Assert.Equal(LauncherCommandKind.Launch, launch.Command!.Kind);
        Assert.Equal("p1", launch.Command.ProfileSelector);
    }

    [Fact]
    public void 解析Profile查询选择和删除命令()
    {
        var show = LauncherCommandParser.Parse(["profile", "show", "p1"]);
        var select = LauncherCommandParser.Parse(["profile", "select", "星铁 国服"]);
        var remove = LauncherCommandParser.Parse(["profile", "remove", "p1"]);

        Assert.Equal(LauncherCommandKind.ProfileShow, show.Command!.Kind);
        Assert.Equal("p1", show.Command.ProfileSelector);
        Assert.Equal(LauncherCommandKind.ProfileSelect, select.Command!.Kind);
        Assert.Equal("星铁 国服", select.Command.ProfileSelector);
        Assert.Equal(LauncherCommandKind.ProfileRemove, remove.Command!.Kind);
    }

    [Fact]
    public void 解析Profile创建与完整Hoyo设置()
    {
        var result = LauncherCommandParser.Parse(
        [
            "profile", "add",
            "--id", "genshin-cn",
            "--name", "原神 国服",
            "--game", "genshin",
            "--exe", @"D:\Games\YuanShen.exe",
            "--fps", "off",
            "--target-fps", "144",
            "--background-fps-limit", "off",
            "--background-fps", "30",
            "--priority", "high",
            "--follow-in-game-preset", "on",
            "--preset-30-fps", "60",
            "--preset-60-fps", "1000",
            "--touch-ui-scale-override", "on",
            "--touch-ui-scale", "350",
        ]);

        var command = Assert.IsType<LauncherCommand>(result.Command);
        var mutation = Assert.IsType<ProfileMutation>(command.ProfileMutation);
        Assert.True(result.Success);
        Assert.Equal(LauncherCommandKind.ProfileAdd, command.Kind);
        Assert.Equal("genshin-cn", mutation.Id);
        Assert.Equal(GameType.Genshin, mutation.Game);
        Assert.False(mutation.FpsUnlockEnabled);
        Assert.Equal(144, mutation.TargetFps);
        Assert.False(mutation.BackgroundFpsLimitEnabled);
        Assert.Equal(ProcessPriority.High, mutation.ProcessPriority);
        Assert.True(mutation.GenshinFollowInGamePreset);
        Assert.Equal(350, mutation.GenshinTouchUiScalePercent);
    }

    [Fact]
    public void 解析Profile修改命令()
    {
        var result = LauncherCommandParser.Parse(
        [
            "profile", "set", "p1",
            "--name", "新的名称",
            "--target-fps", "120",
            "--priority", "belowNormal",
        ]);

        var command = result.Command!;
        Assert.True(result.Success);
        Assert.Equal(LauncherCommandKind.ProfileSet, command.Kind);
        Assert.Equal("p1", command.ProfileSelector);
        Assert.Equal("新的名称", command.ProfileMutation!.DisplayName);
        Assert.Equal(120, command.ProfileMutation.TargetFps);
        Assert.Equal(ProcessPriority.BelowNormal, command.ProfileMutation.ProcessPriority);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("list", "extra")]
    [InlineData("launch", "a", "b")]
    [InlineData("profile", "add", "--id", "p1")]
    [InlineData("profile", "add", "--id", "p1", "--id", "p2", "--name", "x", "--game", "genshin", "--exe", "D:\\Games\\YuanShen.exe")]
    [InlineData("profile", "add", "--id", "p1", "--name", "x", "--game", "genshin", "--exe", "D:\\Games\\YuanShen.exe", "--unknown", "x")]
    [InlineData("profile", "add", "--id", "p1", "--name", "x", "--game", "genshin", "--exe", "D:\\Games\\YuanShen.exe", "--fps")]
    [InlineData("profile", "add", "--id", "p1", "--name", "x", "--game", "genshin", "--exe", "D:\\Games\\YuanShen.exe", "--variant", "cn")]
    [InlineData("profile", "set", "p1", "--id", "renamed")]
    [InlineData("profile", "set", "p1", "--game", "starRail")]
    [InlineData("profile", "set", "p1", "--variant", "cn")]
    [InlineData("profile", "set", "p1", "--target-fps", "9")]
    public void 拒绝未知重复缺失和越界参数(params string[] args)
    {
        Assert.False(LauncherCommandParser.Parse(args).Success);
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
