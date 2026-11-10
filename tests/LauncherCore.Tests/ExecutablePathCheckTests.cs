using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;
using Xunit;

namespace LauncherCore.Tests;

public class ExecutablePathCheckTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白路径视为未设置(string? path) =>
        Assert.Equal(ExecutablePathIssue.Empty, ExecutablePathCheck.Check(GameType.Genshin, path, _ => true));

    [Fact]
    public void 相对路径被拒绝() =>
        Assert.Equal(ExecutablePathIssue.NotFullyQualified,
            ExecutablePathCheck.Check(GameType.Genshin, "YuanShen.exe", _ => true));

    [Fact]
    public void 文件名不在对应游戏白名单时被拒绝() =>
        Assert.Equal(ExecutablePathIssue.NotAllowed,
            ExecutablePathCheck.Check(GameType.StarRail, @"D:\Games\YuanShen.exe", _ => true));

    [Fact]
    public void 白名单内但文件不存在() =>
        Assert.Equal(ExecutablePathIssue.NotFound,
            ExecutablePathCheck.Check(GameType.Genshin, @"D:\Games\YuanShen.exe", _ => false));

    [Fact]
    public void 合法且存在时无问题且文件名大小写不敏感()
    {
        Assert.Equal(ExecutablePathIssue.None,
            ExecutablePathCheck.Check(GameType.Genshin, @"D:\Games\yuanshen.EXE", _ => true));
        Assert.Equal(string.Empty, ExecutablePathCheck.Describe(GameType.Genshin, ExecutablePathIssue.None));
    }

    [Fact]
    public void 每种问题都有提示文案()
    {
        foreach (var issue in new[] { ExecutablePathIssue.Empty, ExecutablePathIssue.NotFullyQualified, ExecutablePathIssue.NotAllowed, ExecutablePathIssue.NotFound })
        {
            Assert.NotEmpty(ExecutablePathCheck.Describe(GameType.ZenlessZoneZero, issue));
        }
    }
}
