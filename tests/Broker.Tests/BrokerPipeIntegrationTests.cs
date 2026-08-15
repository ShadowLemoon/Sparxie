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
            "src", "Sparxie.SessionHost", "bin", "Debug", "net10.0-windows", "Sparxie.SessionHost.exe");
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
                Variant = "intl",
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
                Variant = "cn",
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
}
