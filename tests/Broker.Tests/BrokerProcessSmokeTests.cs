using System.Diagnostics;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>跨进程冒烟：真实启动 Sparxie.Broker.exe，经命名管道 Ping。</summary>
public sealed class BrokerProcessSmokeTests : IAsyncLifetime
{
    private Process? _process;
    private readonly string _pipeName = $"sparxie-smoke-{Guid.NewGuid():N}";

    public Task InitializeAsync()
    {
        var brokerExe = LocateBrokerExe();
        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(brokerExe)!,
        };
        psi.Environment["SPARXIE_PIPE_NAME"] = _pipeName;

        _process = Process.Start(psi);
        Assert.NotNull(_process);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
    }

    [Fact]
    public async Task 真实Broker进程可经命名管道Ping()
    {
        var client = CreateClient();

        PingResponse? response = null;
        Exception? lastError = null;

        // Broker 启动与 Kestrel 就绪需要时间，轮询连接。
        for (var i = 0; i < 60 && response is null; i++)
        {
            try
            {
                response = await client.PingAsync(new PingRequest
                {
                    ProtocolVersion = RpcContract.ProtocolVersion,
                    RequestId = Guid.NewGuid().ToString("N"),
                });
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(250);
            }
        }

        Assert.NotNull(response);
        Assert.Equal(RpcContract.ProtocolVersion, response.ProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(response.BrokerVersion));

        // 进程仍存活
        Assert.False(_process!.HasExited);
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

    private static string TestConfiguration =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
        ?? throw new InvalidOperationException("Test output does not have a configuration directory.");

    private static string LocateBrokerExe()
    {
        var dir = AppContext.BaseDirectory;
        var root = new DirectoryInfo(dir);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var candidate = Path.Combine(root.FullName,
            "src", "Sparxie.Broker", "bin", TestConfiguration, "net10.0-windows", "Sparxie.Broker.exe");
        Assert.True(File.Exists(candidate), $"未找到 {candidate}");
        return candidate;
    }
}
