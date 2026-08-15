using System.Diagnostics;
using Sparxie.Contracts.Rpc;

namespace Sparxie.Broker.Hosting;

/// <summary>启动 SessionHost 进程：继承 Broker 的管理员权限，通过环境变量传递会话契约。</summary>
public static class SessionLauncher
{
    /// <summary>测试/调试可用环境变量覆盖 SessionHost.exe 位置；发布包中与 Broker 同目录。</summary>
    public const string SessionHostExeEnv = "SPARXIE_SESSIONHOST_EXE";

    public static Process Launch(string sessionId, ProfileSnapshot profile)
    {
        var hostExe = Environment.GetEnvironmentVariable(SessionHostExeEnv)
            ?? Path.Combine(AppContext.BaseDirectory, "Sparxie.SessionHost.exe");
        if (!File.Exists(hostExe))
        {
            throw new FileNotFoundException("未找到 Sparxie.SessionHost.exe", hostExe);
        }

        var pipeName = HostEnvironment.PipeNameFor(sessionId);
        var psi = new ProcessStartInfo
        {
            FileName = hostExe,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.Environment[HostEnvironment.SessionId] = sessionId;
        psi.Environment[HostEnvironment.HostPipeName] = pipeName;
        psi.Environment[HostEnvironment.ProfileJson] = ProfileJson.Format(profile);
        // 统一应用根目录：恢复记录 recovery/zzz 与 Broker 同目录（发布包内两者本就同目录）
        psi.Environment["SPARXIE_APP_DIR"] = AppContext.BaseDirectory;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("启动 SessionHost 失败");

        return process;
    }
}
