using Grpc.Core;
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
public sealed class BrokerPipeIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private readonly string _pipeName = $"sparxie-test-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenNamedPipe(_pipeName, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });
        builder.Services.AddGrpc();
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
