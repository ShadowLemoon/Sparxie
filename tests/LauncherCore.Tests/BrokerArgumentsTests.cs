using Sparxie.Contracts.Rpc;

namespace LauncherCore.Tests;

public sealed class BrokerArgumentsTests
{
    [Fact]
    public void 生成并解析受控管道参数()
    {
        var argument = BrokerProcessArguments.Build("sparxie-test-123");

        Assert.True(BrokerProcessArguments.TryParse([argument], out var pipeName, out var error));
        Assert.Equal("sparxie-test-123", pipeName);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void 拒绝未知参数和非法管道名()
    {
        Assert.False(BrokerProcessArguments.TryParse(["--bad=value"], out _, out _));
        Assert.False(BrokerProcessArguments.TryParse(["--pipe-name=a/b"], out _, out _));
        Assert.False(BrokerProcessArguments.TryParse([], out _, out _));
    }
}
