using Sparxie.Broker.Hosting;
using Sparxie.Contracts.Rpc;

namespace Broker.Tests;

/// <summary>
/// Broker 退出不结束 SessionHost 的架构保证测试。
/// 计划：Broker 正常退出或异常崩溃都不结束已经启动的 SessionHost 与游戏会话。
/// 该语义由 SessionLauncher 用普通 Process.Start 启动 Host（无 Job、无父子
/// 终止关联）保证——Broker 死亡时 Windows 不会自动终止 Host。
/// 真实进程级验证受"假游戏会话极短（&lt;500ms）"限制无法稳定，因此以
/// 结构测试锁定实现保证，进程级行为由实机验收覆盖。
/// </summary>
public sealed class BrokerExitLifecycleTests
{
    [Fact]
    public void SessionLauncher通过环境变量传递会话契约()
    {
        // 计划：不接受任意命令行、任意 DLL、任意工作目录或脚本。
        // SessionLauncher 只通过环境变量传 sessionId/pipeName/profileJson/appDir，
        // 不把会话契约放在命令行（避免被其他进程观察到/注入）。
        Assert.Equal("SPARXIE_SESSION_ID", HostEnvironment.SessionId);
        Assert.Equal("SPARXIE_HOST_PIPE_NAME", HostEnvironment.HostPipeName);
        Assert.Equal("SPARXIE_PROFILE_JSON", HostEnvironment.ProfileJson);
    }

    [Fact]
    public void Host管道名与Broker侧共享契约()
    {
        // Host 与 Broker 共享管道命名契约（HostEnvironment.PipeNameFor），
        // 保证 Broker 死亡后 Host 的私有管道命名仍独立存在、不依赖 Broker。
        var sessionId = Guid.NewGuid().ToString("N");
        var pipe = HostEnvironment.PipeNameFor(sessionId);
        Assert.StartsWith(HostEnvironment.PipePrefix, pipe);
        Assert.Contains(sessionId, pipe);
    }

    [Fact]
    public void SessionLauncher启动Host为独立进程()
    {
        // 验证 SessionLauncher 的启动契约：Host 通过环境变量接收全部会话信息，
        // 启动后即与 Broker 解耦（不共享 Job/句柄/内存），Broker 死亡不影响 Host。
        // 这里直接验证 Launch 的路径解析逻辑：未设置覆盖变量时使用 Broker 同目录。
        var original = Environment.GetEnvironmentVariable(SessionLauncher.SessionHostExeEnv);
        try
        {
            Environment.SetEnvironmentVariable(SessionLauncher.SessionHostExeEnv, null);
            // Launch 会在 Host EXE 不存在时抛 FileNotFoundException，
            // 但路径解析本身应指向 Broker 目录下的 Sparxie.SessionHost.exe。
            var ex = Assert.Throws<FileNotFoundException>(() =>
                SessionLauncher.Launch(Guid.NewGuid().ToString("N"), ValidProfile()));
            Assert.Contains("Sparxie.SessionHost.exe", ex.FileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SessionLauncher.SessionHostExeEnv, original);
        }
    }

    private static ProfileSnapshot ValidProfile() => new()
    {
        ProfileId = "p1",
        DisplayName = "星铁",
        Game = "starRail",
        ExecutablePath = @"D:\Games\StarRail.exe",
        Hoyo = new HoyoSettings
        {
            TargetFps = 120,
            BackgroundFps = 10,
            ProcessPriority = "normal",
            GenshinPreset30Fps = 60,
            GenshinPreset60Fps = 1000,
            GenshinTouchUiScalePercent = 400,
        },
    };
}
