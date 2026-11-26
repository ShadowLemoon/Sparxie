using System.Diagnostics;
using Sparxie.Contracts.Rpc;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

/// <summary>绝区零触屏开关：关闭时跳过 ZZZTouchCore/ZZZTouchRuntime 注入，直接启动游戏。</summary>
public sealed class ZzzGameControllerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sparxie-zzz-ctrl-" + Guid.NewGuid().ToString("N"));

    public ZzzGameControllerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    [Fact]
    public void 缺省或false时执行注入_true时跳过注入()
    {
        var omitted = new ProfileSnapshot { Game = "zenlessZoneZero" };
        var enabled = new ProfileSnapshot { Game = "zenlessZoneZero", DisableTouchUi = false };
        var disabled = new ProfileSnapshot { Game = "zenlessZoneZero", DisableTouchUi = true };

        Assert.True(ZzzGameController.ShouldInject(omitted));
        Assert.True(ZzzGameController.ShouldInject(enabled));
        Assert.False(ZzzGameController.ShouldInject(disabled));
    }

    [Fact]
    public async Task 关闭触屏时Install不加载也不注入ZZZTouchCore()
    {
        var fakeExe = Path.Combine(_tempDir, "ZenlessZoneZero.exe");
        File.WriteAllText(fakeExe, "x");

        using var controller = new ZzzGameController("s-zzz-no-touch");
        var snapshot = new ProfileSnapshot
        {
            ProfileId = "zzz-pc",
            DisplayName = "绝区零 键鼠",
            Game = "zenlessZoneZero",
            ExecutablePath = fakeExe,
            DisableTouchUi = true,
        };

        await controller.PrepareLaunchAsync(snapshot, CancellationToken.None);
        Assert.True(controller.DisableTouchUi);

        // SessionHost.Tests 输出目录没有 ZZZTouchCore.dll；若此处尝试 P/Invoke 注入会立刻抛 DllNotFoundException。
        await controller.InstallAsync(Process.GetCurrentProcess(), IntPtr.Zero, null, CancellationToken.None);
        await controller.PostInstallAsync(CancellationToken.None);
    }
}
