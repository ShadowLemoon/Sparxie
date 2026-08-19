using System.Diagnostics;
using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class LauncherProcessIntegrationTests
{
    [Fact]
    public async Task LauncherCore可启动真实Broker并收到SessionHost失败终态()
    {
        var brokerExe = LocateProjectExe("Sparxie.Broker", "Sparxie.Broker.exe");
        var sessionHostExe = LocateProjectExe("Sparxie.SessionHost", "Sparxie.SessionHost.exe");
        Process? brokerProcess = null;

        var options = new BrokerClientOptions
        {
            BrokerExecutablePath = brokerExe,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            RetryDelay = TimeSpan.FromMilliseconds(100),
            ProcessStarter = startInfo =>
            {
                var nonElevated = new ProcessStartInfo
                {
                    FileName = startInfo.FileName,
                    UseShellExecute = false,
                    WorkingDirectory = startInfo.WorkingDirectory,
                    CreateNoWindow = true,
                };
                var pipeName = startInfo.Arguments["--pipe-name=".Length..];
                nonElevated.Environment["SPARXIE_PIPE_NAME"] = pipeName;
                nonElevated.Environment["SPARXIE_SESSIONHOST_EXE"] = sessionHostExe;
                brokerProcess = Process.Start(nonElevated);
                Assert.NotNull(brokerProcess);
                return Process.GetProcessById(brokerProcess.Id);
            },
        };

        try
        {
            await using var launcher = new LauncherClient(new BrokerClient(options: options));
            var profile = new GameProfile
            {
                Id = "integration",
                DisplayName = "不存在的星铁",
                Game = GameType.StarRail,
                ExecutablePath = Path.Combine(Path.GetTempPath(), "StarRail.exe"),
                Hoyo = new HoyoProfileSettings(),
            };

            var session = await launcher.StartSessionAsync(profile);
            var states = new List<string>();
            await foreach (var ev in session.Events)
            {
                states.Add(ev.State);
                if (ev.State is "Failed" or "Exited" or "HostCrashedBeforeRunning" or "HostCrashedAfterRunning")
                {
                    break;
                }
            }

            Assert.Contains("Failed", states);
        }
        finally
        {
            if (brokerProcess is { HasExited: false })
            {
                await brokerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            brokerProcess?.Dispose();
        }
    }

    private static string TestConfiguration =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
        ?? throw new InvalidOperationException("Test output does not have a configuration directory.");

    private static string LocateProjectExe(string project, string executable)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "src", project, "bin", TestConfiguration, "net10.0-windows", executable);
        Assert.True(File.Exists(path), $"未找到 {path}");
        return path;
    }
}
