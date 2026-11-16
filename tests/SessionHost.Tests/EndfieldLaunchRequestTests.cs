using System.Runtime.InteropServices;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

public sealed class EndfieldLaunchRequestTests
{
    [Fact]
    public void 触控模式请求启用触屏并保留高帧率和显示参数()
    {
        var settings = new HoyoProfileSettings
        {
            EndfieldInputMode = "touch",
            FpsUnlockEnabled = true,
            TargetFps = 240,
            EndfieldWidth = 2560,
            EndfieldHeight = 1440,
            EndfieldRefreshRate = 165,
            EndfieldAutoResolution = false,
        };

        var request = EndfieldGameController.BuildRequest(@"C:\Games\Endfield.exe", settings);
        try
        {
            Assert.Equal(1, request.TouchUiEnabled);
            Assert.Equal(1, request.FpsUnlockEnabled);
            Assert.Equal(240, request.TargetFps);
            Assert.Equal(2560, request.Width);
            Assert.Equal(1440, request.Height);
            Assert.Equal(165, request.RefreshRate);
            Assert.Equal(@"C:\Games\Endfield.exe", Marshal.PtrToStringUni(request.GameExecutablePath));
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 请求布局与EndfieldCore的ABI一致()
    {
        // ENDFIELD_REQUEST_SIZE = 48 bytes: eight 4-byte fields, the path pointer at 32 and its length at 40 (padded to 48)
        Assert.Equal(48, Marshal.SizeOf<EndfieldGameController.EndfieldLaunchRequest>());
        Assert.Equal(32, (int)Marshal.OffsetOf<EndfieldGameController.EndfieldLaunchRequest>(nameof(EndfieldGameController.EndfieldLaunchRequest.GameExecutablePath)));
        Assert.Equal(40, (int)Marshal.OffsetOf<EndfieldGameController.EndfieldLaunchRequest>(nameof(EndfieldGameController.EndfieldLaunchRequest.GameExecutablePathChars)));
    }

    [Fact]
    public void PC模式请求关闭触屏但仍可解锁帧率()
    {
        var request = EndfieldGameController.BuildRequest(
            @"C:\Games\Endfield.exe",
            new HoyoProfileSettings { EndfieldInputMode = "pc", TargetFps = 144 });
        try
        {
            Assert.Equal(0, request.TouchUiEnabled);
            Assert.Equal(1, request.FpsUnlockEnabled);
            Assert.Equal(144, request.TargetFps);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 不发送Endfield字段的旧启动器得到模型默认值而不是0x0()
    {
        var model = new HoyoSettings { FpsUnlockEnabled = true, TargetFps = 144 }.ToModel()!;
        Assert.Equal("touch", model.EndfieldInputMode);
        Assert.True(model.EndfieldAutoResolution);
        Assert.Equal(1920, model.EndfieldWidth);
        Assert.Equal(1080, model.EndfieldHeight);
        Assert.Equal(60, model.EndfieldRefreshRate);

        // 显式发送的值不被覆盖。
        var explicitModel = new HoyoSettings
        {
            EndfieldInputMode = "pc", EndfieldAutoResolution = false, EndfieldWidth = 2560, EndfieldHeight = 1440, EndfieldRefreshRate = 165,
        }.ToModel()!;
        Assert.Equal("pc", explicitModel.EndfieldInputMode);
        Assert.False(explicitModel.EndfieldAutoResolution);
        Assert.Equal(2560, explicitModel.EndfieldWidth);
        Assert.Equal(165, explicitModel.EndfieldRefreshRate);
    }

    [Theory]
    [InlineData("auto", "touch")]
    [InlineData("", "touch")]
    [InlineData("touch", "touch")]
    [InlineData("pc", "pc")]
    public void 已经去掉的自动模式按触控处理(string stored, string expected)
    {
        Assert.Equal(expected, new HoyoSettings { EndfieldInputMode = stored, FpsUnlockEnabled = true, TargetFps = 144, EndfieldWidth = 1920, EndfieldHeight = 1080, EndfieldRefreshRate = 60 }.ToModel()!.EndfieldInputMode);
    }
}
