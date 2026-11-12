using System.Diagnostics;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// End to end with the real Broker, the real SessionHost and a dummy game that records its own command line: the screen arguments of StartSession are
/// on the game's command line. The Zenless Zone Zero path creates the process in SessionHost itself. Needs the packaged component (the ZZZ runtime DLLs are
/// not in the build output): set SPARXIE_E2E_PACKAGE_DIR to the folder with Sparxie.Broker.exe; without it the test does nothing.
/// The dummy game has no window, so the session ends in a failure after the record is written; nothing here starts a real game.
/// </summary>
[Collection("SessionHostProcess")]
public sealed class ScreenArgsGameEndToEndTests : IAsyncLifetime
{
    private readonly string _brokerPipe = $"sparxie-pipe-{Guid.NewGuid():N}";
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "sparxie-e2e-" + Guid.NewGuid().ToString("N"));
    private Process? _broker;

    private static string? PackageDir => Environment.GetEnvironmentVariable("SPARXIE_E2E_PACKAGE_DIR");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_broker is { HasExited: false })
        {
            _broker.Kill(entireProcessTree: true);
            await _broker.WaitForExitAsync();
        }

        _broker?.Dispose();
        for (var i = 0; i < 10; i++)
        {
            try { Directory.Delete(_workDir, recursive: true); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(300); }
        }
    }

    [Theory]
    [InlineData("windowed", 1280, 720, "-screen-width 1280 -screen-height 720 -screen-fullscreen 0")]
    [InlineData("borderless", 1920, 1080, "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -popupwindow")]
    [InlineData("fullscreen", 2868, 1320, "-screen-width 2868 -screen-height 1320 -screen-fullscreen 1")]
    public async Task 绝区零会话的游戏命令行带屏幕参数(string mode, int width, int height, string expected)
    {
        if (string.IsNullOrEmpty(PackageDir))
        {
            return;
        }

        var game = StageFakeGame("ZenlessZoneZero.exe");
        var record = Path.Combine(_workDir, "record.txt");
        StartBroker(record);

        var response = await StartAsync("zenlessZoneZero", game, new ScreenArgs { Width = width, Height = height, WindowMode = mode });
        Assert.True(response.Accepted, response.Message);

        var line = await ReadCommandLineAsync(record);
        Assert.EndsWith(" " + expected, line.TrimEnd());
        // (a managed dummy shows its .dll as the program in Environment.CommandLine; the arguments after it are what the SessionHost put on the command line)
    }

    [Fact]
    public async Task 绝区零会话没有屏幕参数时游戏命令行不带参数()
    {
        if (string.IsNullOrEmpty(PackageDir))
        {
            return;
        }

        var game = StageFakeGame("ZenlessZoneZero.exe");
        var record = Path.Combine(_workDir, "record.txt");
        StartBroker(record);

        var response = await StartAsync("zenlessZoneZero", game, null);
        Assert.True(response.Accepted, response.Message);

        var line = await ReadCommandLineAsync(record);
        Assert.DoesNotContain("-screen-", line);
        Assert.DoesNotContain("-popupwindow", line);
    }

    private string StageFakeGame(string exeName)
    {
        Directory.CreateDirectory(_workDir);
        var source = LocateFakeGameOutput();
        var target = Path.Combine(_workDir, "game");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        // the apphost finds FakeGame.dll beside itself by the name stored inside, so the exe can carry the game's name
        var exe = Path.Combine(target, exeName);
        File.Move(Path.Combine(target, "FakeGame.exe"), exe);
        return exe;
    }

    private void StartBroker(string record)
    {
        var brokerExe = Path.Combine(PackageDir!, "Sparxie.Broker.exe");
        Assert.True(File.Exists(brokerExe), $"未找到 {brokerExe}");
        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = PackageDir!,
        };
        psi.Environment["SPARXIE_PIPE_NAME"] = _brokerPipe;
        psi.Environment["FAKE_GAME_RECORD"] = record;
        psi.Environment["FAKE_GAME_LIFE_MS"] = "100000";
        // the data dir (logs\game-launch.log) of the package stays out of the real one
        psi.Environment["SPARXIE_DATA_DIR"] = Path.Combine(_workDir, "data");
        _broker = Process.Start(psi);
        Assert.NotNull(_broker);
    }

    private static async Task<string> ReadCommandLineAsync(string record)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!File.Exists(record) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.True(File.Exists(record), "假游戏没有记录命令行：游戏进程没有被启动");
        var text = await File.ReadAllTextAsync(record);
        var line = text.Split('\n').First(l => l.StartsWith("cmdline=", StringComparison.Ordinal));
        return line["cmdline=".Length..];
    }

    private async Task<StartSessionResponse> StartAsync(string game, string exe, ScreenArgs? screen)
    {
        var handler = new SocketsHttpHandler { ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_brokerPipe).ConnectAsync(ct) };
        var client = new SparxieBroker.SparxieBrokerClient(GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler }));
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
                        ProfileId = "p-e2e",
                        DisplayName = "屏幕参数端到端",
                        Game = game,
                        ExecutablePath = exe,
                        Hoyo = new HoyoSettings { FpsUnlockEnabled = true, TargetFps = 60, BackgroundFps = 10, ProcessPriority = "normal", GenshinPreset30Fps = 60, GenshinPreset60Fps = 1000, GenshinTouchUiScalePercent = 400 },
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

    private static string LocateFakeGameOutput()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!.Name;
        var directory = Path.Combine(root.FullName, "tests", "FakeGame", "bin", configuration, "net10.0");
        Assert.True(File.Exists(Path.Combine(directory, "FakeGame.exe")), $"未找到 {directory}\\FakeGame.exe（Broker.Tests 的构建会一并构建它）");
        return directory;
    }
}
