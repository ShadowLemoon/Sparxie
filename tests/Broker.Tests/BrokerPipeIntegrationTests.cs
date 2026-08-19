using Grpc.Core;
using System.Diagnostics;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sparxie.Broker.Services;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>真实命名管道 gRPC 集成测试：同进程启动 Kestrel 管道端点，客户端经管道连接。</summary>
[Collection("SessionHostProcess")]
public sealed class BrokerPipeIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private readonly string _pipeName = $"sparxie-test-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(
            Sparxie.Broker.Hosting.SessionLauncher.SessionHostExeEnv,
            LocateSessionHostExe());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenNamedPipe(_pipeName, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<Sparxie.Broker.Sessions.SessionRegistry>();
        builder.Services.AddSingleton(new Sparxie.Broker.Hosting.BrokerLifecycleOptions { Enabled = false });
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.MapGrpcService<BrokerService>();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static string TestConfiguration =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
        ?? throw new InvalidOperationException("Test output does not have a configuration directory.");

    private static string LocateSessionHostExe()
    {
        var dir = AppContext.BaseDirectory;
        var root = new DirectoryInfo(dir);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var candidate = Path.Combine(root.FullName,
            "src", "Sparxie.SessionHost", "bin", TestConfiguration, "net10.0-windows", "Sparxie.SessionHost.exe");
        Assert.True(File.Exists(candidate), $"未找到 {candidate}");
        return candidate;
    }

    private SparxieBroker.SparxieBrokerClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_pipeName).ConnectAsync(ct),
        };
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        return new SparxieBroker.SparxieBrokerClient(channel);
    }

    [Fact]
    public async Task Ping成功()
    {
        var client = CreateClient();
        var response = await client.PingAsync(new PingRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
        });

        Assert.Equal(RpcContract.ProtocolVersion, response.ProtocolVersion);
        Assert.Equal(BrokerService.BrokerVersion, response.BrokerVersion);
    }

    [Fact]
    public async Task 协议版本不匹配被拒绝()
    {
        var client = CreateClient();
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.PingAsync(new PingRequest
            {
                ProtocolVersion = 999,
                RequestId = Guid.NewGuid().ToString("N"),
            }));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task StartSession合法快照被接受()
    {
        var client = CreateClient();
        var events = new List<SessionEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
            },
        });

        Assert.True(response.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(response.SessionId));

        // 等待 Host 完成会话（EXE 不存在 → Failed 事件），保证后台连接完成且 Host 正常退出
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                if (events.Any(e => e.State is "Exited" or "Failed"))
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
        Assert.Contains(snapshot, e => e.ErrorCode == (int)ErrorCode.ExecutableNotFound);

        // 会话结束事件到达后，Host 应自行退出（StopApplication）
        var hostDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < hostDeadline
               && Process.GetProcessesByName("Sparxie.SessionHost").Length > 0)
        {
            await Task.Delay(200);
        }

        Assert.Empty(Process.GetProcessesByName("Sparxie.SessionHost"));

        cts.Cancel();
        try
        {
            await eventTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Grpc.Core.RpcException)
        {
        }
    }

    [Fact]
    public async Task StartSession非法快照被拒绝()
    {
        var client = CreateClient();
        var response = await client.StartSessionAsync(new StartSessionRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Profile = new ProfileSnapshot
            {
                ProfileId = "p1",
                DisplayName = "坏配置",
                Game = "genshin",
                ExecutablePath = @"D:\Games\YuanShen.exe",
                Hoyo = new HoyoSettings { TargetFps = 9999 },
            },
        });

        Assert.False(response.Accepted);
        Assert.Equal((int)ErrorCode.InvalidArgument, response.ErrorCode);
    }

    [Fact]
    public async Task SetTargetFps当前无会话返回SessionNotFound()
    {
        var client = CreateClient();
        var response = await client.SetTargetFpsAsync(new SetTargetFpsRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = "none",
            TargetFps = 120,
        });

        Assert.False(response.Applied);
        Assert.Equal((int)ErrorCode.SessionNotFound, response.ErrorCode);
    }

    [Fact]
    public async Task 已运行游戏存在时拒绝启动()
    {
        // 假游戏进程：复制 ping.exe 为白名单内 StarRail.exe 并启动
        var fakeDir = Path.Combine(Path.GetTempPath(), "sparxie-fake-running", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeDir);
        var fakeExe = Path.Combine(fakeDir, "StarRail.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), fakeExe);
        using var game = Process.Start(new ProcessStartInfo
        {
            FileName = fakeExe,
            Arguments = "-t 127.0.0.1",
            WorkingDirectory = fakeDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(game);
        Assert.False(game.HasExited);

        try
        {
            var client = CreateClient();
            var events = new List<SessionEvent>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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

            // Broker 接受（已运行检测在 SessionHost 侧），Host 检测到游戏在跑 → 拒绝
            var response = await client.StartSessionAsync(new StartSessionRequest
            {
                ProtocolVersion = RpcContract.ProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
                Profile = new ProfileSnapshot
                {
                    ProfileId = "p1",
                    DisplayName = "星铁",
                    Game = "starRail",
                    ExecutablePath = fakeExe,
                    Hoyo = new HoyoSettings
                    {
                        TargetFps = 120,
                        BackgroundFps = 10,
                        ProcessPriority = "normal",
                        GenshinPreset30Fps = 60,
                        GenshinPreset60Fps = 1000,
                        GenshinTouchUiScalePercent = 400,
                    },
                },
            });
            Assert.True(response.Accepted);

            // 等待事件流出现 Failed(GameAlreadyRunning)
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (events)
                {
                    if (events.Any(e => e.State is "Failed" or "Exited"))
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

            Assert.Contains(snapshot, e => e.State == "Failed"
                && e.ErrorCode == (int)ErrorCode.GameAlreadyRunning);

            cts.Cancel();
            try
            {
                await eventTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Grpc.Core.RpcException)
            {
            }
        }
        finally
        {
            if (game is { HasExited: false })
            {
                game.Kill(entireProcessTree: true);
                await game.WaitForExitAsync();
            }

            try
            {
                Directory.Delete(fakeDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task 同款游戏第二会话被拒绝()
    {
        var client = CreateClient();
        var events = new List<SessionEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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

        // 第一个会话：不存在的 EXE → Host 快速 Failed，但互斥在 Running 前已获取
        var first = await client.StartSessionAsync(new StartSessionRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Profile = new ProfileSnapshot
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
            },
        });
        Assert.True(first.Accepted);

        // 第二个会话（同款游戏）在第一个 Host 仍持有互斥时启动 → 管理员侧拒绝
        var second = await client.StartSessionAsync(new StartSessionRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Profile = new ProfileSnapshot
            {
                ProfileId = "p2",
                DisplayName = "星铁2",
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
            },
        });
        Assert.False(second.Accepted);
        Assert.Equal((int)ErrorCode.MutexConflict, second.ErrorCode);

        // 等第一个会话结束，避免残留 Host
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                if (events.Any(e => e.State is "Failed" or "Exited"))
                {
                    break;
                }
            }

            await Task.Delay(100);
        }

        cts.Cancel();
        try
        {
            await eventTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Grpc.Core.RpcException)
        {
        }
    }

    [Fact]
    public async Task 不同游戏并行会话互不冲突()
    {
        var client = CreateClient();
        var events = new List<SessionEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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

        // 三款游戏同时启动：游戏级互斥域相互独立，均被接受
        var profiles = new[]
        {
            new ProfileSnapshot
            {
                ProfileId = "p-genshin",
                DisplayName = "原神",
                Game = "genshin",
                ExecutablePath = @"D:\Games\YuanShen.exe",
                Hoyo = new HoyoSettings
                {
                    TargetFps = 120, BackgroundFps = 10, ProcessPriority = "normal",
                    GenshinPreset30Fps = 60, GenshinPreset60Fps = 1000, GenshinTouchUiScalePercent = 400,
                },
            },
            new ProfileSnapshot
            {
                ProfileId = "p-sr",
                DisplayName = "星铁",
                Game = "starRail",
                ExecutablePath = @"D:\Games\StarRail.exe",
                Hoyo = new HoyoSettings
                {
                    TargetFps = 120, BackgroundFps = 10, ProcessPriority = "normal",
                    GenshinPreset30Fps = 60, GenshinPreset60Fps = 1000, GenshinTouchUiScalePercent = 400,
                },
            },
            new ProfileSnapshot
            {
                ProfileId = "p-zzz",
                DisplayName = "绝区零",
                Game = "zenlessZoneZero",
                ExecutablePath = @"D:\Games\ZenlessZoneZero.exe",
            },
        };

        foreach (var profile in profiles)
        {
            var response = await client.StartSessionAsync(new StartSessionRequest
            {
                ProtocolVersion = RpcContract.ProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
                Profile = profile,
            });
            Assert.True(response.Accepted, $"{profile.Game} 应被接受: {response.Message}");
        }

        // 等待三个会话都结束（EXE 不存在 → 各自 Failed），验证互不干扰且各自独立收尾
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                var failedIds = events.Where(e => e.State == "Failed")
                    .Select(e => e.SessionId).ToHashSet();
                if (failedIds.Count >= 3)
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

        var failedGames = snapshot.Where(e => e.State == "Failed")
            .Select(e => e.SessionId).Distinct().Count();
        Assert.True(failedGames >= 3, $"三个会话都应结束，实际 {failedGames}: {string.Join(" → ", snapshot.Select(e => e.State))}");

        cts.Cancel();
        try
        {
            await eventTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Grpc.Core.RpcException)
        {
        }
    }
}
