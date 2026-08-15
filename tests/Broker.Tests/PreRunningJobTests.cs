using System.Diagnostics;
using Sparxie.Infrastructure.Jobs;

namespace Broker.Tests;

/// <summary>Running 前失效保护 Job 的生命周期验证。</summary>
public class PreRunningJobTests
{
    private static Process StartSleepingProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 30 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    [Fact]
    public void 关闭句柄时killOnClose终止进程()
    {
        using var game = StartSleepingProcess();
        try
        {
            using (var job = PreRunningJob.Create())
            {
                job.Assign(game.SafeHandle);
            } // 句柄关闭 → 进程被终止

            game.WaitForExit(5000);
            Assert.True(game.HasExited, "KILL_ON_JOB_CLOSE 应终止 Job 内进程");
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill();
            }

            game.Dispose();
        }
    }

    [Fact]
    public void 撤销killOnClose后关闭句柄进程存活()
    {
        using var game = StartSleepingProcess();
        try
        {
            using (var job = PreRunningJob.Create())
            {
                job.Assign(game.SafeHandle);
                job.ReleaseKillOnClose(); // 进入 Running：撤销杀进程语义
            } // 句柄关闭 → 进程应存活

            Thread.Sleep(500);
            Assert.False(game.HasExited, "撤销后关闭句柄不应终止进程");
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill();
            }

            game.Dispose();
        }
    }

    [Fact]
    public void 撤销操作可读回验证()
    {
        using var game = StartSleepingProcess();
        try
        {
            using var job = PreRunningJob.Create();
            job.Assign(game.SafeHandle);
            job.ReleaseKillOnClose(); // 内部读回验证标志已清除，失败会抛异常

            // 连续两次撤销应幂等
            job.ReleaseKillOnClose();
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill();
            }

            game.Dispose();
        }
    }
}
