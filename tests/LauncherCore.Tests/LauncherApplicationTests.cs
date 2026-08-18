using Sparxie.Infrastructure.Configuration;
using Sparxie.Launcher;

namespace LauncherCore.Tests;

public sealed class LauncherApplicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sparxie-launcher-cli", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public LauncherApplicationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响断言结论。
        }
    }

    [Fact]
    public async Task 从空配置通过CLI创建修改选择删除并原子保存()
    {
        var add = await RunAsync(
            "profile", "add",
            "--id", "genshin-cn",
            "--name", "原神 国服",
            "--game", "genshin",
            "--exe", @"D:\Games\YuanShen.exe");

        Assert.Equal(0, add.ExitCode);
        Assert.Contains("已创建 Profile：genshin-cn", add.Output);
        Assert.True(File.Exists(ConfigPath));

        var afterAdd = new AppConfigStore(ConfigPath).Load().Config;
        var genshin = Assert.Single(afterAdd.Profiles);
        Assert.Equal("genshin-cn", afterAdd.SelectedProfileId);
        Assert.Equal(120, genshin.Hoyo!.TargetFps);

        var set = await RunAsync(
            "profile", "set", "genshin-cn",
            "--name", "原神 · 国服",
            "--target-fps", "144",
            "--background-fps-limit", "off",
            "--priority", "high");
        Assert.Equal(0, set.ExitCode);
        Assert.Contains("已更新 Profile：genshin-cn", set.Output);

        var show = await RunAsync("profile", "show", "genshin-cn");
        Assert.Equal(0, show.ExitCode);
        Assert.Contains("name: 原神 · 国服", show.Output);
        Assert.Contains("targetFps: 144", show.Output);
        Assert.Contains("backgroundFpsLimitEnabled: off", show.Output);
        Assert.Contains("processPriority: high", show.Output);

        var addZzz = await RunAsync(
            "profile", "add",
            "--id", "zzz-cn",
            "--name", "绝区零 国服",
            "--game", "zenlessZoneZero",
            "--exe", @"D:\Games\ZenlessZoneZero.exe");
        Assert.Equal(0, addZzz.ExitCode);

        var select = await RunAsync("profile", "select", "zzz-cn");
        Assert.Equal(0, select.ExitCode);

        var list = await RunAsync("list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("*\tzzz-cn", list.Output);

        var remove = await RunAsync("profile", "remove", "zzz-cn");
        Assert.Equal(0, remove.ExitCode);

        var persisted = new AppConfigStore(ConfigPath).Load().Config;
        var remaining = Assert.Single(persisted.Profiles);
        Assert.Equal("genshin-cn", remaining.Id);
        Assert.Equal("genshin-cn", persisted.SelectedProfileId);
        Assert.Equal(144, remaining.Hoyo!.TargetFps);
        Assert.False(remaining.Hoyo.BackgroundFpsLimitEnabled);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await LauncherApplication.RunAsync(args, input, output, error, ConfigPath);
        return (exitCode, output.ToString(), error.ToString());
    }
}
