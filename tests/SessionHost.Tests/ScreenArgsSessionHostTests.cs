using System.Runtime.InteropServices;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

/// <summary>The session's screen arguments on their way into the Hoyo launch request and the launch log.</summary>
public sealed class ScreenArgsSessionHostTests
{
    private static readonly ScreenArgs Windowed = new() { Width = 1280, Height = 720, WindowMode = "windowed" };

    [Fact]
    public void Hoyo请求结构是88字节且参数指针在偏移72()
    {
        Assert.Equal(88, Marshal.SizeOf<HoyoGameController.HoyoLaunchRequest>());
        Assert.Equal(72, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.GameArguments)));
        Assert.Equal(80, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.GameArgumentsChars)));
    }

    [Fact]
    public void Hoyo请求携带屏幕参数文本()
    {
        var request = HoyoGameController.BuildRequest("starRail", @"D:\Games\StarRail.exe", new HoyoProfileSettings(), Windowed);
        try
        {
            const string expected = "-screen-width 1280 -screen-height 720 -screen-fullscreen 0";
            Assert.Equal(expected, Marshal.PtrToStringUni(request.GameArguments, (int)request.GameArgumentsChars));
            Assert.Equal((uint)expected.Length, request.GameArgumentsChars);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
            Marshal.FreeHGlobal(request.GameArguments);
        }
    }

    [Fact]
    public void Hoyo请求没有屏幕参数时参数指针为空()
    {
        var request = HoyoGameController.BuildRequest("genshin", @"D:\Games\YuanShen.exe", new HoyoProfileSettings());
        try
        {
            Assert.Equal(IntPtr.Zero, request.GameArguments);
            Assert.Equal(0u, request.GameArgumentsChars);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

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
