using System.Runtime.InteropServices;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

/// <summary>快照（跨进程契约）→ HoyoProfileSettings → native 启动请求 的映射链。</summary>
public sealed class HoyoLaunchRequestMappingTests
{
    [Fact]
    public void 快照缺省触屏字段按开启处理()
    {
        var model = new HoyoSettings { FpsUnlockEnabled = true, TargetFps = 120 }.ToModel()!;
        Assert.True(model.TouchUiEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 快照显式触屏字段原样映射(bool touchUiEnabled)
    {
        var model = new HoyoSettings { FpsUnlockEnabled = true, TargetFps = 120, TouchUiEnabled = touchUiEnabled }.ToModel()!;
        Assert.Equal(touchUiEnabled, model.TouchUiEnabled);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void 启动请求携带触屏载荷开关_取反存放(bool touchUiEnabled, int expectedDisabled)
    {
        var request = HoyoGameController.BuildRequest(
            "starRail", @"D:\Games\StarRail.exe", new HoyoProfileSettings { TouchUiEnabled = touchUiEnabled });
        try
        {
            Assert.Equal(expectedDisabled, request.TouchUiDisabled);
            Assert.Equal(1, request.GameType);
            Assert.Equal(0, request.Reserved0);
            Assert.Equal((uint)Marshal.SizeOf<HoyoGameController.HoyoLaunchRequest>(), request.Size);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 无Hoyo设置时请求缺省触屏开启()
    {
        var request = HoyoGameController.BuildRequest("genshin", @"D:\Games\YuanShen.exe", null);
        try
        {
            Assert.Equal(0, request.TouchUiDisabled);
            Assert.Equal(0, request.GameType);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 请求结构布局与C头一致()
    {
        // hoyo_touch_core_abi.h（x64）：game_executable_path_chars 在偏移 64，game_arguments 在 72，game_arguments_chars 在 80；
        // 追加的 touch_ui_disabled 在 84，reserved0 在 88，整个结构 96 字节。
        // 旧调用方（size=88）不含这两个字段：native 按 size 判断，不读它们；字段取反存放，全零（新调用方没设置）也等于既有行为。
        Assert.Equal(96, Marshal.SizeOf<HoyoGameController.HoyoLaunchRequest>());
        Assert.Equal(64, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.GameExecutablePathChars)));
        Assert.Equal(72, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.GameArguments)));
        Assert.Equal(80, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.GameArgumentsChars)));
        Assert.Equal(84, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.TouchUiDisabled)));
        Assert.Equal(88, (int)Marshal.OffsetOf<HoyoGameController.HoyoLaunchRequest>(nameof(HoyoGameController.HoyoLaunchRequest.Reserved0)));
    }
}
