using Sparxie.Infrastructure.Diagnostics;
using Xunit;

namespace Config.Tests;

public class DiagnosticExporterTests
{
    [Fact]
    public void 完整用户路径被脱敏()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"game at {user}\\AppData\\LocalLow\\miHoYo\\GENERAL_DATA.bin and {user}\\Documents";
        var result = DiagnosticExporter.Redact(text);

        Assert.DoesNotContain(user, result);
        Assert.Contains("<USER>", result);
    }

    [Fact]
    public void 凭据样式被脱敏()
    {
        var text = "Authorization: Bearer abc123def456 token=xyz secret=hunter2";
        var result = DiagnosticExporter.Redact(text);

        Assert.DoesNotContain("abc123def456", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("<REDACTED>", result);
    }

    [Fact]
    public void 盘符路径被脱敏()
    {
        var text = @"log path: D:\Games\Genshin Impact Game\YuanShen.exe done";
        var result = DiagnosticExporter.Redact(text);

        Assert.DoesNotContain(@"D:\Games", result);
        Assert.Contains("<PATH>", result);
    }

    [Fact]
    public void 敏感路径被排除()
    {
        Assert.True(DiagnosticExporter.IsExcluded(@"C:\app\config.json"));
        Assert.True(DiagnosticExporter.IsExcluded(@"C:\app\config.invalid-20260815-abc123.json"));
        Assert.True(DiagnosticExporter.IsExcluded(@"C:\app\recovery\zzz\session\manifest.json"));
        Assert.False(DiagnosticExporter.IsExcluded(@"C:\app\logs\broker.log"));
    }

    [Fact]
    public void 导出只包含日志与环境信息()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "sparxie-diag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(appRoot, "logs"));
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllText(Path.Combine(appRoot, "logs", "broker.log"), $"path {user}\\secret.txt\n");
        File.WriteAllText(Path.Combine(appRoot, "config.json"), "{\"secret\": true}");

        var target = Path.Combine(appRoot, "out");
        var files = DiagnosticExporter.Export(appRoot, target);

        var logContent = File.ReadAllText(Path.Combine(target, "logs", "broker.log"));
        Assert.DoesNotContain(user, logContent);
        Assert.True(File.Exists(Path.Combine(target, "environment.txt")));
        Assert.False(File.Exists(Path.Combine(target, "config.json")));
    }
}
