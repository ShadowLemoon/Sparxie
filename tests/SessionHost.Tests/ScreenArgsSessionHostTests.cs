using Sparxie.Contracts.Rpc;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

/// <summary>The launch log line of a session.</summary>
public sealed class ScreenArgsSessionHostTests
{
    private static readonly ScreenArgs Windowed = new() { Width = 1280, Height = 720, WindowMode = "windowed" };

    [Fact]
    public void 启动日志行写出游戏与参数()
    {
        var profile = new ProfileSnapshot { ProfileId = "zzz-cn-1", DisplayName = "绝区零", Game = "zenlessZoneZero", ExecutablePath = @"D:\g\ZenlessZoneZero.exe", Screen = Windowed };
        var line = LaunchLog.Line(profile, new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        Assert.Contains("game=zenlessZoneZero", line);
        Assert.Contains("exe=ZenlessZoneZero.exe", line);
        Assert.Contains("screen-args=-screen-width 1280 -screen-height 720 -screen-fullscreen 0", line);
        profile.Screen = null;
        Assert.Contains("screen-args=(none)", LaunchLog.Line(profile, DateTimeOffset.UnixEpoch));
    }
}
