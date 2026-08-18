using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;
using Sparxie.Infrastructure.Zzz;
using Xunit;

namespace Broker.Tests;

/// <summary>
/// ZZZ 失败路径集成：真实 Broker + 真实 SessionHost + 假绝区零安装目录。
/// 注入阶段因 ZZZTouchCore.dll 不存在而失败，验证：
/// 1) PrepareLaunch 已写入恢复记录并切换触屏配置；
/// 2) 失败后本次会话 AbortAsync 恢复 PC 配置并删除恢复记录；
/// 3) 无遗留恢复记录。
/// </summary>
[Collection("SessionHostProcess")]
public sealed class ZzzRecoveryIntegrationTests : IAsyncLifetime
{
    private Process? _brokerProcess;
    private readonly string _brokerPipe = $"sparxie-pipe-{Guid.NewGuid():N}";
    private readonly string _fakeGameDir = Path.Combine(Path.GetTempPath(), "sparxie-zzz-fakegame", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        // 假绝区零安装目录：白名单 EXE + 合法 Sleepy 编码 GENERAL_DATA.bin（PC 模式）
        var dataDir = Path.Combine(_fakeGameDir, "ZenlessZoneZero_Data", "Persistent", "LocalStorage");
        Directory.CreateDirectory(dataDir);
        var generalDataPath = Path.Combine(dataDir, "GENERAL_DATA.bin");
        ZzzGeneralData.WriteRawString(generalDataPath, "{\"LocalUILayoutPlatform\": 2}");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"),
            Path.Combine(_fakeGameDir, "ZenlessZoneZero.exe"));

        Environment.SetEnvironmentVariable(
            Sparxie.Broker.Hosting.SessionLauncher.SessionHostExeEnv,
            LocateSessionHostExe());

        var brokerExe = LocateBrokerExe();
        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(brokerExe)!,
        };
        psi.Environment["SPARXIE_PIPE_NAME"] = _brokerPipe;
        _brokerProcess = Process.Start(psi);
        Assert.NotNull(_brokerProcess);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_brokerProcess is { HasExited: false })
        {
            _brokerProcess.Kill(entireProcessTree: true);
            await _brokerProcess.WaitForExitAsync();
        }

        _brokerProcess?.Dispose();

        // 清理测试留下的恢复记录
        foreach (var record in ZzzRecoveryStore.FindAll())
        {
            if (record.GeneralDataPath.StartsWith(_fakeGameDir, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ZzzRecoveryStore.Delete(record);
                }
                catch
                {
                }
            }
        }

        try
        {
            Directory.Delete(_fakeGameDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ZZZ注入失败后本次会话恢复PC配置且无遗留记录()
    {
        var generalDataPath = Path.Combine(
            _fakeGameDir, "ZenlessZoneZero_Data", "Persistent", "LocalStorage", "GENERAL_DATA.bin");
        var client = CreateBrokerClient();
        var events = new List<SessionEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var eventTask = Task.Run(async () =>
        {
            var stream = client.StreamEvents(new StreamEventsRequest
            {
                ProtocolVersion = RpcContract.ProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
            }, cancellationToken: cts.Token);
            await foreach (var ev in stream.ResponseStream.ReadAllAsync(cts.Token))
            {
                lock (events)
                {
                    events.Add(ev);
                }
            }
        });

        var response = await client.StartSessionAsync(new StartSessionRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Profile = new ProfileSnapshot
            {
                ProfileId = "zzz1",
                DisplayName = "假绝区零",
                Game = "zenlessZoneZero",
                Variant = "cn",
                ExecutablePath = Path.Combine(_fakeGameDir, "ZenlessZoneZero.exe"),
            },
        }, cancellationToken: cts.Token);

        Assert.True(response.Accepted, response.Message);
        var sessionId = response.SessionId;

        // 等待失败事件
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                if (events.Any(e => e.State == "Failed"))
                {
                    break;
                }
            }

            await Task.Delay(100);
        }

        List<SessionEvent> snapshot;
        lock (events)
        {
            snapshot = [.. events];
        }

        Assert.Contains(snapshot, e => e.State == "Failed");

        // Host 进程自行退出意味着 finally（Cleanup + AbortAsync 恢复配置）已执行完
        var hostDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < hostDeadline
               && Process.GetProcessesByName("Sparxie.SessionHost").Length > 0)
        {
            await Task.Delay(200);
        }

        Assert.Empty(Process.GetProcessesByName("Sparxie.SessionHost"));

        // 注入失败 → 失败路径 AbortAsync 恢复 PC 配置并删除恢复记录
        Assert.Equal(ZzzGeneralData.PlatformPc, ZzzGeneralData.ReadPlatform(generalDataPath));
        Assert.Null(ZzzRecoveryStore.TryLoad(sessionId));

        cts.Cancel();
        try
        {
            await eventTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException)
        {
        }
    }

    private SparxieBroker.SparxieBrokerClient CreateBrokerClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_brokerPipe).ConnectAsync(ct),
        };
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        return new SparxieBroker.SparxieBrokerClient(channel);
    }

    private static string TestConfiguration =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
        ?? throw new InvalidOperationException("Test output does not have a configuration directory.");

    private static string LocateBrokerExe()
    {
        var root = LocateRepoRoot();
        var candidate = Path.Combine(root.FullName,
            "src", "Sparxie.Broker", "bin", TestConfiguration, "net10.0-windows", "Sparxie.Broker.exe");
        Assert.True(File.Exists(candidate), $"未找到 {candidate}");
        return candidate;
    }

    private static string LocateSessionHostExe()
    {
        var root = LocateRepoRoot();
        var candidate = Path.Combine(root.FullName,
            "src", "Sparxie.SessionHost", "bin", TestConfiguration, "net10.0-windows", "Sparxie.SessionHost.exe");
        Assert.True(File.Exists(candidate), $"未找到 {candidate}");
        return candidate;
    }

    private static DirectoryInfo LocateRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        var root = new DirectoryInfo(dir);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root;
    }
}
