using System.Diagnostics;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// Real Broker + a stand-in SessionHost that writes the profile it was handed: the screen arguments travel StartSession → Broker validation → the Host's
/// environment, an older client that sends none gives the Host none, and a bad screen object is refused before any Host is started.
/// </summary>
[Collection("SessionHostProcess")]
public sealed class ScreenArgsBrokerTests : IAsyncLifetime
{
    private readonly string _brokerPipe = $"sparxie-pipe-{Guid.NewGuid():N}";
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "sparxie-screen-" + Guid.NewGuid().ToString("N"));
    private Process? _broker;

    private string FakeHostOut => Path.Combine(_workDir, "profile.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workDir);
        var fakeHost = Path.Combine(_workDir, "fake-host.cmd");
        File.WriteAllText(
            fakeHost,
            "@echo off\r\n" +
            "powershell -NoProfile -NonInteractive -Command \"[IO.File]::WriteAllText($env:SPARXIE_FAKE_HOST_OUT, $env:SPARXIE_PROFILE_JSON)\"\r\n");

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
        psi.Environment[Sparxie.Broker.Hosting.SessionLauncher.SessionHostExeEnv] = fakeHost;
        psi.Environment["SPARXIE_FAKE_HOST_OUT"] = FakeHostOut;
        _broker = Process.Start(psi);
        Assert.NotNull(_broker);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_broker is { HasExited: false })
        {
            _broker.Kill(entireProcessTree: true);
            await _broker.WaitForExitAsync();
        }

        _broker?.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task 屏幕参数抵达SessionHost()
    {
        var response = await StartAsync(new ScreenArgs { Width = 1280, Height = 720, WindowMode = "windowed" });
        Assert.True(response.Accepted, response.Message);
        var received = await ReadHostProfileAsync();
        Assert.Equal(1280, received.Screen.Width);
        Assert.Equal(720, received.Screen.Height);
        Assert.Equal("windowed", received.Screen.WindowMode);
    }

    [Fact]
    public async Task 旧客户端不发屏幕参数时Host看到的是缺省()
    {
        var response = await StartAsync(null);
        Assert.True(response.Accepted, response.Message);
        var received = await ReadHostProfileAsync();
        Assert.Null(received.Screen);
        Assert.Equal(string.Empty, ScreenArguments.Build(received.Screen));
    }

    [Fact]
    public async Task 非法屏幕参数被Broker拒绝且不启动Host()
    {
        var response = await StartAsync(new ScreenArgs { Width = 1280, Height = 0, WindowMode = "windowed" });
        Assert.False(response.Accepted);
        await Task.Delay(1500);
        Assert.False(File.Exists(FakeHostOut), "被拒绝的会话不应启动 SessionHost");
    }

    private async Task<ProfileSnapshot> ReadHostProfileAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(FakeHostOut) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(File.Exists(FakeHostOut), "假 SessionHost 没有收到 SPARXIE_PROFILE_JSON");
        await Task.Delay(300);
        return ProfileJson.Parse(await File.ReadAllTextAsync(FakeHostOut));
    }

    private async Task<PingResponse> PingAsync()
    {
        var client = CreateBrokerClient();
        for (var i = 0; i < 120; i++)
        {
            try
            {
                return await client.PingAsync(new PingRequest { ProtocolVersion = RpcContract.ProtocolVersion, RequestId = Guid.NewGuid().ToString("N") }, deadline: DateTime.UtcNow.AddSeconds(2));
            }
            catch (Grpc.Core.RpcException)
            {
                await Task.Delay(250);
            }
        }

        throw new TimeoutException("Broker 没有应答 Ping");
    }

    private async Task<StartSessionResponse> StartAsync(ScreenArgs? screen)
    {
        var client = CreateBrokerClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Exception? last = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                return await client.StartSessionAsync(new StartSessionRequest
                {
                    ProtocolVersion = RpcContract.ProtocolVersion,
                    RequestId = Guid.NewGuid().ToString("N"),
                    Profile = new ProfileSnapshot
                    {
                        ProfileId = "p-screen",
                        DisplayName = "屏幕参数",
                        Game = "starRail",
                        ExecutablePath = @"D:\Games\StarRail.exe",
                        Hoyo = new HoyoSettings { FpsUnlockEnabled = true, TargetFps = 120, BackgroundFps = 10, ProcessPriority = "normal", GenshinPreset30Fps = 60, GenshinPreset60Fps = 1000, GenshinTouchUiScalePercent = 400 },
                        Screen = screen,
                    },
                }, cancellationToken: cts.Token);
            }
            catch (Grpc.Core.RpcException ex)
            {
                last = ex;
                await Task.Delay(250);
            }
        }

        throw new TimeoutException(last?.Message ?? "StartSession did not answer");
    }

    private SparxieBroker.SparxieBrokerClient CreateBrokerClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_brokerPipe).ConnectAsync(ct),
        };
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        return new SparxieBroker.SparxieBrokerClient(channel);
    }

    private static string TestConfiguration =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
        ?? throw new InvalidOperationException("Test output does not have a configuration directory.");

    private static string LocateBrokerExe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var candidate = Path.Combine(root.FullName, "src", "Sparxie.Broker", "bin", TestConfiguration, "net10.0-windows", "Sparxie.Broker.exe");
        Assert.True(File.Exists(candidate), $"未找到 {candidate}");
        return candidate;
    }
}
