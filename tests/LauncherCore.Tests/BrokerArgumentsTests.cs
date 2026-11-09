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

    [Fact]
    public void 可选的父进程PID随管道名一起往返()
    {
        var arguments = BrokerProcessArguments.Build("sparxie-test-123", 4321).Split(' ');

        Assert.True(BrokerProcessArguments.TryParse(arguments, out var pipeName, out var parentPid, out var error));
        Assert.Equal("sparxie-test-123", pipeName);
        Assert.Equal(4321, parentPid);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void 没有父进程PID时仍然合法_且拒绝非法PID()
    {
        Assert.True(BrokerProcessArguments.TryParse(["--pipe-name=sparxie-test-123"], out _, out var parentPid, out _));
        Assert.Null(parentPid);
        Assert.False(BrokerProcessArguments.TryParse(["--pipe-name=sparxie-test-123", "--parent-pid=0"], out _, out _, out _));
        Assert.False(BrokerProcessArguments.TryParse(["--pipe-name=sparxie-test-123", "--parent-pid=abc"], out _, out _, out _));
        Assert.False(BrokerProcessArguments.TryParse(["--pipe-name=sparxie-test-123", "--other=1"], out _, out _, out _));
    }
}
