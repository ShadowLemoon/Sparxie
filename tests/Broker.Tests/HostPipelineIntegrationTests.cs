using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// 全链路集成：真实 Broker + 真实 SessionHost + 假游戏进程，
/// 验证 StartSession → Host 私有管道 → 事件转发 → Failed（Hoyo 假游戏
/// bootstrap 真实执行失败，不误报成功）的完整闭环。
/// </summary>
[Collection("SessionHostProcess")]
public sealed class HostPipelineIntegrationTests : IAsyncLifetime
{
    private Process? _brokerProcess;
    private Process? _hostProcess;
    private readonly string _brokerPipe = $"sparxie-pipe-{Guid.NewGuid():N}";
    private readonly string _fakeGameDir = Path.Combine(Path.GetTempPath(), "sparxie-fakegame", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        // 假游戏：复制 ping.exe 并命名为白名单内的 StarRail.exe（无参数运行立即退出）
        Directory.CreateDirectory(_fakeGameDir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), FakeGamePath);

        // SessionHost.exe 不在测试输出目录：显式指向其构建输出
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

    private string FakeGamePath => Path.Combine(_fakeGameDir, "StarRail.exe");

    public async Task DisposeAsync()
    {
        foreach (var process in new[] { _brokerProcess, _hostProcess })
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
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
    public async Task Hoyo假游戏失败路径事件流到达Failed()
    {
        var client = CreateBrokerClient();
        var events = new List<SessionEvent>();
        var failedEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 事件流使用独立的总体兜底超时，不能与 Broker 冷启动重试共享 20 秒预算。
        using var eventCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventTask = Task.Run(async () =>
        {
            var stream = client.StreamEvents(new StreamEventsRequest
            {
                ProtocolVersion = RpcContract.ProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
            }, cancellationToken: eventCts.Token);
            await foreach (var ev in stream.ResponseStream.ReadAllAsync(eventCts.Token))
            {
                lock (events)
                {
                    events.Add(ev);
                }

                if (ev.State == "Failed")
                {
                    failedEvent.TrySetResult(true);
                }
            }
        });

        // 启动会话（Broker 冷启动 JIT 可能较慢，单独给足 30 秒轮询窗口）。
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        StartSessionResponse startResponse = null!;
        Exception? lastError = null;
        for (var i = 0; i < 120 && startResponse is null && !startCts.IsCancellationRequested; i++)
        {
            try
            {
                startResponse = await client.StartSessionAsync(new StartSessionRequest
                {
                    ProtocolVersion = RpcContract.ProtocolVersion,
                    RequestId = Guid.NewGuid().ToString("N"),
                    Profile = new ProfileSnapshot
                    {
                        ProfileId = "p1",
                        DisplayName = "假星铁",
                        Game = "starRail",
                        ExecutablePath = FakeGamePath,
                        Hoyo = new HoyoSettings
                        {
                            FpsUnlockEnabled = true,
                            TargetFps = 120,
                            BackgroundFps = 10,
                            ProcessPriority = "normal",
                            GenshinPreset30Fps = 60,
                            GenshinPreset60Fps = 1000,
                            GenshinTouchUiScalePercent = 400,
                        },
                    },
                }, cancellationToken: startCts.Token);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (!startCts.IsCancellationRequested)
                {
                    await Task.Delay(250);
                }
            }
        }

        Assert.NotNull(startResponse);
        Assert.True(startResponse.Accepted, lastError?.Message);
        _hostProcess = Process.GetProcessesByName("Sparxie.SessionHost").FirstOrDefault();

        // 等待真正的 Failed 事件，而不是反复轮询共享列表；超时只限制失败路径本身。
        var completed = await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        var failedObserved = completed == failedEvent.Task;

        List<SessionEvent> snapshot;
        lock (events)
        {
            snapshot = [.. events];
        }

        eventCts.Cancel();
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

        var states = string.Join(" → ", snapshot.Select(e => e.State));
        Assert.True(failedObserved, $"30 秒内未收到 Failed，实际事件: {states}");
        Assert.Contains(snapshot, e => e.State == "Starting");
        Assert.Contains(snapshot, e => e.State == "Failed");
        Assert.DoesNotContain(snapshot, e => e.State is "Running" or "Exited");
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
