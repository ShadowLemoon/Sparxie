using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// 真实 Broker + 假 SessionHost：假 Host 只把 Broker 通过 SPARXIE_PROFILE_JSON 传来的快照写入文件，
/// 证明 touch_ui_enabled 经 StartSession → Broker 校验 → Host 环境变量 抵达 SessionHost。
/// </summary>
[Collection("SessionHostProcess")]
public sealed class TouchUiFieldBrokerTests : IAsyncLifetime
{
    private readonly string _brokerPipe = $"sparxie-pipe-{Guid.NewGuid():N}";
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "sparxie-touchui-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData(false, true)]  // PC 界面：显式 false 必须抵达 Host
    [InlineData(true, true)]   // 触屏：显式 true
    [InlineData(null, false)]  // 旧客户端不发字段：Host 看到「缺省」，由 Host 解释为开启
    public async Task 触屏开关抵达SessionHost(bool? touchUiEnabled, bool expectPresent)
    {
        var hoyo = new HoyoSettings
        {
            FpsUnlockEnabled = true,
            TargetFps = 144,
            BackgroundFps = 10,
            ProcessPriority = "normal",
            GenshinPreset30Fps = 60,
            GenshinPreset60Fps = 1000,
            GenshinTouchUiScalePercent = 400,
        };
        if (touchUiEnabled is { } value)
        {
            hoyo.TouchUiEnabled = value;
        }

        var client = CreateBrokerClient();
        StartSessionResponse? started = null;
        Exception? lastError = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (started is null && !cts.IsCancellationRequested)
        {
            try
            {
                started = await client.StartSessionAsync(new StartSessionRequest
                {
                    ProtocolVersion = RpcContract.ProtocolVersion,
                    RequestId = Guid.NewGuid().ToString("N"),
                    Profile = new ProfileSnapshot
                    {
                        ProfileId = "p-touch",
                        DisplayName = "触屏开关",
                        Game = "starRail",
                        ExecutablePath = @"D:\Games\StarRail.exe",
                        Hoyo = hoyo,
                    },
                }, cancellationToken: cts.Token);
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(250);
            }
        }

        Assert.True(started?.Accepted == true, lastError?.Message ?? started?.Message);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(FakeHostOut) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(File.Exists(FakeHostOut), "假 SessionHost 没有收到 SPARXIE_PROFILE_JSON");
        // 写文件可能与读取交错，稍等一次再读。
        await Task.Delay(300);
        var received = ProfileJson.Parse(await File.ReadAllTextAsync(FakeHostOut));

        Assert.Equal("starRail", received.Game);
        Assert.Equal(144, received.Hoyo.TargetFps);
        Assert.Equal(expectPresent, received.Hoyo.HasTouchUiEnabled);
        if (touchUiEnabled is { } expected)
        {
            Assert.Equal(expected, received.Hoyo.TouchUiEnabled);
        }
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
        var candidate = Path.Combine(root.FullName,
            "src", "Sparxie.Broker", "bin", TestConfiguration, "net10.0-windows", "Sparxie.Broker.exe");
        Assert.True(File.Exists(candidate), $"未找到 {candidate}");
        return candidate;
    }
}
