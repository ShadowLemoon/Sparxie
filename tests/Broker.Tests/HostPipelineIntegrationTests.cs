using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// 全链路集成：真实 Broker + 真实 SessionHost + 缺失的白名单游戏路径，
/// 验证 StartSession → Host 私有管道 → SessionHost Validation Failed → Broker 事件转发。
/// Hoyo native launch 失败路径由 HoyoAbi.Tests 单独覆盖，避免把上游扫描耗时耦合进管道测试。
/// </summary>
[Collection("SessionHostProcess")]
public sealed class HostPipelineIntegrationTests : IAsyncLifetime
{
    private Process? _brokerProcess;
    private readonly string _brokerPipe = $"sparxie-pipe-{Guid.NewGuid():N}";
    private readonly string _missingGamePath = Path.Combine(
        Path.GetTempPath(), "sparxie-missinggame", Guid.NewGuid().ToString("N"), "StarRail.exe");

    public Task InitializeAsync()
    {
        // SessionHost.exe 不在测试输出目录：显式指向其构建输出。
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
        // 只清理由本测试直接启动的 Broker 进程树。不要按进程名全局查找 SessionHost，
        // 否则并行运行的其他测试程序集也可能有同名 Host，被误杀后形成跨测试竞态。
        if (_brokerProcess is { HasExited: false })
        {
            _brokerProcess.Kill(entireProcessTree: true);
            await _brokerProcess.WaitForExitAsync();
        }

        _brokerProcess?.Dispose();
    }

    [Fact]
    public async Task Hoyo不存在游戏失败路径事件流到达Failed()
    {
        var client = CreateBrokerClient();
        var events = new List<SessionEvent>();
        var failedEvent = new TaskCompletionSource<SessionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 事件流使用独立总体兜底超时，不能与 Broker 冷启动重试共享预算。
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
                    failedEvent.TrySetResult(ev);
                }
            }
        });

        // Broker 冷启动单独给足 30 秒轮询窗口。
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
                        DisplayName = "不存在的星铁",
                        Game = "starRail",
                        // Broker 只校验白名单文件名；文件存在性由真实 SessionHost 校验，
                        // 因此这里可以稳定覆盖 Broker → Host → Failed 的完整转发链路，
                        // 而不进入耗时不确定的 native bootstrap 扫描。
                        ExecutablePath = _missingGamePath,
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

        // 缺失 EXE 应在 SessionHost Validation 阶段确定性地产生 Failed。
        var completed = await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(30)));

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
        Assert.True(completed == failedEvent.Task, $"30 秒内未收到 Failed，实际事件: {states}");

        var failed = await failedEvent.Task;
        Assert.Equal((int)StageCode.Validation, failed.Stage);
        Assert.Equal((int)ErrorCode.ExecutableNotFound, failed.ErrorCode);
        Assert.Contains(snapshot, e => e.State == "Failed");
        Assert.DoesNotContain(snapshot, e => e.State is "Starting" or "Running" or "Exited");
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
