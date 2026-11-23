using Sparxie.Desktop.ViewModels;

namespace Desktop.Tests;

public sealed class LaunchErrorInfoTests
{
    [Theory]
    [InlineData("Failed", true)]
    [InlineData("HostCrashedBeforeRunning", true)]
    [InlineData("HostCrashedAfterRunning", true)]
    [InlineData("Exited", false)]
    [InlineData("Running", false)]
    public void 只有失败终态才算错误(string state, bool failure) => Assert.Equal(failure, LaunchErrorInfo.IsFailureState(state));

    [Theory]
    [InlineData("假父进程失败：数字签名无效", "厂商签名")]
    [InlineData("插件调用超时", "没有在规定时间内响应")]
    [InlineData("插件返回错误", "插件执行出错")]
    [InlineData("注册表备份尚未恢复", "注册表")]
    [InlineData("拒绝访问路径", "管理员权限")]
    [InlineData("找不到文件 x.exe", "找不到需要的文件")]
    [InlineData("乱七八糟", "重试一次")]
    public void 按消息关键词给出可执行的建议(string message, string expectedHint)
    {
        var info = LaunchErrorInfo.FromStartFailure("配置 1", message);

        Assert.Contains(expectedHint, info.Hint);
        Assert.Equal(message, info.Detail);
        Assert.Contains("配置 1", info.Title);
    }

    [Fact]
    public void 宿主崩溃和空消息也有可读标题()
    {
        var info = LaunchErrorInfo.FromSessionEnd("HostCrashedAfterRunning", null);

        Assert.Contains("宿主进程", info.Title);
        Assert.Equal("HostCrashedAfterRunning", info.Detail);
    }

    [Fact]
    public void 诊断文本只含这次错误()
    {
        var text = LaunchErrorInfo.FromStartFailure("配置 1", "boom").ToDiagnosticText("终末地", new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("终末地", text);
        Assert.Contains("boom", text);
        Assert.Contains("2026-11-23", text);
    }
}
