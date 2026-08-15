using Microsoft.Extensions.Logging;
using Sparxie.Infrastructure.Logging;
using Xunit;

namespace Config.Tests;

public class RollingFileLoggerTests : IDisposable
{
    private readonly string _appRoot;

    public RollingFileLoggerTests()
    {
        _appRoot = Path.Combine(Path.GetTempPath(), "sparxie-logging", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_appRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void 日志写入当日文件()
    {
        using var provider = new RollingFileLoggerProvider(_appRoot, "broker");
        var logger = provider.CreateLogger("Test.Category");
        logger.LogInformation("hello {Value}", 42);

        var file = Path.Combine(_appRoot, "logs", $"broker-{DateTime.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("hello 42", content);
        Assert.Contains("Test.Category", content);
    }

    [Fact]
    public void 过期日志被清理()
    {
        // 构造 8 天前的日志文件
        var logsDir = Path.Combine(_appRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var old = Path.Combine(logsDir, $"broker-{DateTime.Now.AddDays(-8):yyyyMMdd}.log");
        var fresh = Path.Combine(logsDir, $"broker-{DateTime.Now:yyyyMMdd}.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(fresh, "fresh");

        using var provider = new RollingFileLoggerProvider(_appRoot, "broker", retentionDays: 7);
        provider.CreateLogger("t").LogInformation("trigger");

        Assert.False(File.Exists(old), "8 天前日志应被清理");
        Assert.True(File.Exists(fresh), "当日日志应保留");
    }

    [Fact]
    public void 当日日志不被清理()
    {
        var logsDir = Path.Combine(_appRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var today = Path.Combine(logsDir, $"host-{DateTime.Now:yyyyMMdd}.log");
        File.WriteAllText(today, "x");

        using var provider = new RollingFileLoggerProvider(_appRoot, "host", retentionDays: 7);
        provider.CreateLogger("t").LogInformation("trigger");

        Assert.True(File.Exists(today));
    }

    [Fact]
    public void 非法文件名被清理()
    {
        var logsDir = Path.Combine(_appRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var weird = Path.Combine(logsDir, "broker-notadate.log");
        File.WriteAllText(weird, "x");

        using var provider = new RollingFileLoggerProvider(_appRoot, "broker");
        provider.CreateLogger("t").LogInformation("trigger");

        // 无法解析日期的不应被误删
        Assert.True(File.Exists(weird));
    }
}
