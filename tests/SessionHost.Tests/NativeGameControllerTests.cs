using System.Diagnostics;
using System.Text;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Plugins;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

/// <summary>插件关闭触屏界面的可选支持：非 createsProcess 插件未声明能力时由宿主跳过加载；createsProcess 插件未声明能力时明确拒绝。</summary>
public sealed class NativeGameControllerTests
{
    private static PluginEntry CreateEntry(string capsJson, PluginAvailability availability = PluginAvailability.Untrusted)
    {
        var json = $$"""{ "id": "ext.acme.demo", "displayName": "演示插件", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": {{capsJson}} }""";
        Assert.True(PluginManifest.TryParse(Encoding.UTF8.GetBytes(json), "ext.acme.demo", out var manifest, out var error), error);
        return new PluginEntry("ext.acme.demo", @"C:\nonexistent\ext.acme.demo", availability, null, manifest, "00", "00", 1);
    }

    private static ProfileSnapshot CreateSnapshot(bool disableTouchUi) => new()
    {
        ProfileId = "plug-1",
        DisplayName = "演示",
        Game = "ext.acme.demo",
        ExecutablePath = @"C:\Games\Demo.exe",
        DisableTouchUi = disableTouchUi,
    };

    [Fact]
    public async Task 未声明disableTouchUi的非自建进程插件_关闭触屏时宿主直接跳过加载()
    {
        using var controller = new NativeGameController(CreateEntry("""["injects", "hotParams"]"""));

        Assert.False(controller.CreatesProcess);
        Assert.False(controller.SupportsDisableTouchUi);

        // entry 是 Untrusted 且路径不存在；若尝试加载插件 DLL 会抛出 PluginLoadException。
        await controller.PrepareLaunchAsync(CreateSnapshot(disableTouchUi: true), CancellationToken.None);
        Assert.True(controller.SkippedPlugin);

        await controller.InstallAsync(Process.GetCurrentProcess(), IntPtr.Zero, null, CancellationToken.None);
        await controller.PostInstallAsync(CancellationToken.None);
        await controller.AbortAsync(CancellationToken.None);
        await Assert.ThrowsAsync<NotSupportedException>(() => controller.SetParamAsync("targetFps", 120, CancellationToken.None));
    }

    [Fact]
    public async Task 未声明disableTouchUi的自建进程插件_关闭触屏时明确拒绝()
    {
        using var controller = new NativeGameController(CreateEntry("""["createsProcess", "injects"]"""));

        Assert.True(controller.CreatesProcess);
        Assert.False(controller.SupportsDisableTouchUi);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            controller.PrepareLaunchAsync(CreateSnapshot(disableTouchUi: true), CancellationToken.None));
        Assert.Contains("disableTouchUi", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(controller.SkippedPlugin);
    }

    [Fact]
    public async Task 声明了disableTouchUi的插件_关闭触屏时走插件加载路径()
    {
        using var controller = new NativeGameController(CreateEntry("""["createsProcess", "injects", "disableTouchUi"]"""));

        Assert.True(controller.CreatesProcess);
        Assert.True(controller.SupportsDisableTouchUi);

        // 不应跳过也不应抛 NotSupportedException；会进入 NativePluginLibrary.Load 校验并因 Untrusted 抛出 PluginLoadException。
        await Assert.ThrowsAsync<PluginLoadException>(() =>
            controller.PrepareLaunchAsync(CreateSnapshot(disableTouchUi: true), CancellationToken.None));
        Assert.False(controller.SkippedPlugin);
    }
}
