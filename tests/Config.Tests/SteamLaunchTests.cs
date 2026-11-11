using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

public sealed class SteamLaunchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-steam-tests", Guid.NewGuid().ToString("N"));

    public SteamLaunchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    [Fact]
    public void 目录里有steam_appid时是Steam安装()
    {
        var exe = Path.Combine(_dir, "ZenlessZoneZero.exe");
        Assert.False(SteamLaunch.IsSteamInstall(exe));

        File.WriteAllText(Path.Combine(_dir, SteamLaunch.AppIdFileName), "4162040\n");

        Assert.True(SteamLaunch.IsSteamInstall(exe));
    }

    [Fact]
    public void Steam启动URI把参数整体转义后跟在双斜杠后()
    {
        var uri = SteamLaunch.BuildUri("4162040", "-popupwindow -foo \"a b\"");

        const string prefix = "steam://run/4162040//";
        Assert.StartsWith(prefix, uri, StringComparison.Ordinal);
        Assert.Equal("-popupwindow -foo \"a b\"", Uri.UnescapeDataString(uri[prefix.Length..]));
        Assert.DoesNotContain(' ', uri);
    }

    [Fact]
    public void 没有参数时URI以双斜杠结尾()
    {
        Assert.Equal("steam://run/4162040//", SteamLaunch.BuildUri("4162040", string.Empty));
    }

    [Fact]
    public void Steam安装在区服识别里叫Steam()
    {
        File.WriteAllText(Path.Combine(_dir, SteamLaunch.AppIdFileName), "4162040");

        Assert.Equal("Steam", ServerLabelDetector.Detect(GameType.ZenlessZoneZero, Path.Combine(_dir, "ZenlessZoneZero.exe")));
    }

    [Fact]
    public void 识别Steam日志里的无许可启动失败()
    {
        const string log = "[2026-11-11 23:32:17] SteamUI: WARNING: LaunchGameAction: launch error 1 4162040 LaunchApp AppError_5\n";

        Assert.True(SteamLaunch.TryParseLaunchError(log, "4162040", out var message));
        Assert.Contains("没有这款游戏的许可", message);
    }

    [Fact]
    public void 别的游戏的启动失败和正常日志不算()
    {
        Assert.False(SteamLaunch.TryParseLaunchError("launch error 1 1812620 LaunchApp AppError_5", "4162040", out _));
        Assert.False(SteamLaunch.TryParseLaunchError("OnGameActionUserRequest: 4162040 LaunchApp CreatingProcess", "4162040", out _));
        Assert.False(SteamLaunch.TryParseLaunchError(string.Empty, "4162040", out _));
    }

    [Fact]
    public void 其他错误码给出通用提示()
    {
        Assert.True(SteamLaunch.TryParseLaunchError("launch error 1 4162040 LaunchApp AppError_12", "4162040", out var message));
        Assert.Contains("AppError_12", message);
    }
}
