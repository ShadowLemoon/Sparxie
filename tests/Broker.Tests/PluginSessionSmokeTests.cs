using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// 端到端冒烟：真实 Broker + 真实 SessionHost + 真实加载 mock 插件 DLL（tests/MockGameModule）+ 假游戏进程。
/// 验证：只有用户确认过的插件才能被 Broker 放行；会话走完 Prepare → Install → Running → 热调 → 游戏退出 → 释放的完整链路，
/// 并且插件的所有调用都在同一个专用线程上。
/// mock 未构建（tests/MockGameModule/build_mock.ps1）时直接返回。
/// 做法：把插件放进 Broker 与 SessionHost 各自的数据目录（它们所在的 bin 目录），结束后清理。
/// </summary>
[Collection("SessionHostProcess")]
public sealed class PluginSessionSmokeTests : IAsyncLifetime
{
    private const string PluginId = "ext.mock.game";

    private const string SettingsSchema = """
        { "fields": [
          { "key": "mode", "label": "模式", "type": "enum", "default": "touch", "options": [ { "value": "touch", "label": "触屏" }, { "value": "pc", "label": "键鼠" } ] },
          { "key": "autoResolution", "label": "跟随串流", "type": "bool", "default": true },
          { "key": "width", "label": "宽度", "type": "int", "min": 320, "max": 7680, "default": 1920, "showWhen": { "key": "autoResolution", "equals": false } }
        ] }
        """;
    private readonly string _brokerPipe = $"sparxie-plugin-smoke-{Guid.NewGuid():N}";
    private readonly List<string> _installedRoots = [];
    private Process? _brokerProcess;
    private string? _logDir;
    private string? _fakeGameDir;
    private bool _skipped;

    public Task InitializeAsync()
    {
        var mock = MockDir();
        if (mock is null)
        {
            _skipped = true;
            return Task.CompletedTask;
        }

        var brokerExe = Locate("Sparxie.Broker");
        var hostExe = Locate("Sparxie.SessionHost");
        foreach (var exe in new[] { brokerExe, hostExe })
        {
            InstallPlugin(DataRootOf(Path.GetDirectoryName(exe)!), mock);
        }

        _logDir = Path.Combine(Path.GetTempPath(), "sparxie-plugin-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDir);
        _fakeGameDir = Path.Combine(Path.GetTempPath(), "sparxie-plugin-smoke-game", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fakeGameDir);
        File.Copy(Path.Combine(mock, "Mock.exe"), Path.Combine(_fakeGameDir, "Mock.exe"));

        Environment.SetEnvironmentVariable(Sparxie.Broker.Hosting.SessionLauncher.SessionHostExeEnv, hostExe);
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
        foreach (var process in Process.GetProcessesByName("Mock").Where(p => IsOurFakeGame(p)))
        {
            process.Kill();
            process.Dispose();
        }

        // SessionHost 已随 Broker 进程树结束，DLL 句柄释放后才能删。
        await Task.Delay(500);
        foreach (var root in _installedRoots)
        {
            TryDelete(Path.Combine(root, "plugins", PluginId), recursive: true);
            TryDelete(Path.Combine(root, "plugin-trust.json"), recursive: false);
            TryDelete(Path.Combine(root, "plugin-data", PluginId), recursive: true);
            TryDeleteEmptyDirectory(Path.Combine(root, "plugins"));
            TryDeleteEmptyDirectory(Path.Combine(root, "plugin-data"));
        }

        TryDelete(_fakeGameDir, recursive: true);
    }

    [Fact]
    public async Task 已确认的插件游戏完整会话_加载_运行_热调_退出_释放()
    {
        if (_skipped) { return; }

        var client = CreateClient();
        var events = new List<SessionEvent>();
        var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var streamTask = Task.Run(async () =>
        {
            try
            {
                var stream = client.StreamEvents(new StreamEventsRequest
                {
                    ProtocolVersion = RpcContract.ProtocolVersion,
                    RequestId = Guid.NewGuid().ToString("N"),
                }, cancellationToken: streamCts.Token);
                await foreach (var ev in stream.ResponseStream.ReadAllAsync(streamCts.Token))
                {
                    lock (events) { events.Add(ev); }

                    if (ev.State == "Running") { running.TrySetResult(true); }

                    if (ev.State is "Exited" or "Failed") { exited.TrySetResult(ev.State == "Exited"); running.TrySetResult(false); }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or RpcException)
            {
                // 测试结束时取消
            }
        });

        var response = await StartWithRetryAsync(client);
        Assert.True(response.Accepted, response.Message);

        Assert.True(await running.Task.WaitAsync(TimeSpan.FromSeconds(60)), $"没有进入 Running，事件: {Describe(events)}");

        // 热调经 Broker → SessionHost → 插件 set_param
        var fps = await client.SetTargetFpsAsync(new SetTargetFpsRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = response.SessionId,
            TargetFps = 60,
        });
        Assert.True(fps.Applied, fps.Message);

        // 结束假游戏进程 → 会话 Exited → 宿主释放插件会话
        foreach (var process in Process.GetProcessesByName("Mock").Where(IsOurFakeGame))
        {
            process.Kill();
        }

        Assert.True(await exited.Task.WaitAsync(TimeSpan.FromSeconds(60)), $"没有以 Exited 结束，事件: {Describe(events)}");
        streamCts.Cancel();
        await streamTask;

        var calls = await ReadCallsAsync();
        var names = calls.Select(call => call.Name).ToList();
        Assert.Equal("create_session", names[0]);
        Assert.Contains("prepare_launch", names);
        Assert.Contains("install", names);
        Assert.Contains("post_install", names);
        Assert.Contains("set_param", names);
        Assert.Equal("release", names[^1]);
        Assert.True(names.IndexOf("prepare_launch") < names.IndexOf("install"));
        Assert.True(names.IndexOf("install") < names.IndexOf("post_install"));
        Assert.Single(calls.Select(call => call.Thread).Distinct()); // 所有调用在同一个专用线程上
        Assert.DoesNotContain("abort", names); // 正常结束不走失败路径
        Assert.DoesNotContain(events, e => e.State == "Failed");

        var settings = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_installedRoots.Last(), "plugin-data", PluginId, "settings.json")))!.AsObject();
        Assert.Equal("touch", (string)settings["mode"]!); // 枚举不在选项里：回落默认值
        Assert.False((bool)settings["autoResolution"]!);
        Assert.Equal(7680, (int)settings["width"]!); // 夹到 max
        Assert.False(settings.ContainsKey("evil")); // 未知键被丢弃
    }

    [Fact]
    public async Task 未确认的插件游戏被Broker拒绝_不会加载DLL()
    {
        if (_skipped) { return; }

        // 撤销 Broker 与 SessionHost 的信任记录：插件仍在目录里，但处于"未启用"。
        foreach (var root in _installedRoots)
        {
            new PluginTrustStore(Path.Combine(root, "plugin-trust.json")).Remove(PluginId);
        }

        var client = CreateClient();
        var response = await StartWithRetryAsync(client);

        Assert.False(response.Accepted);
        Assert.False(File.Exists(Path.Combine(_logDir!, "calls.log"))); // 从没加载过
    }

    private async Task<StartSessionResponse> StartWithRetryAsync(SparxieBroker.SparxieBrokerClient client)
    {
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
                        ProfileId = "p1",
                        DisplayName = "Mock 游戏",
                        Game = PluginId,
                        ExecutablePath = Path.Combine(_fakeGameDir!, "Mock.exe"),
                        // 故意给越界、类型不对、未知键的设置：宿主必须按 manifest 的 schema 归一化后再传给插件，而不是拒绝会话。
                        SettingsJson = """{ "mode": "gamepad", "autoResolution": false, "width": 99999, "evil": 1 }""",
                    },
                }, cancellationToken: cts.Token);
            }
            catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
            {
                last = ex; // Broker 冷启动
                await Task.Delay(250);
            }
        }

        throw new TimeoutException($"Broker 30 秒内未就绪: {last?.Message}");
    }

    private async Task<List<(string Name, string Thread)>> ReadCallsAsync()
    {
        // 插件 data_dir 就是 SessionHost 的 plugin-data\<id>；日志在那里。
        var hostRoot = _installedRoots.Last(); // SessionHost 的数据根
        var path = Path.Combine(hostRoot, "plugin-data", PluginId, "calls.log");
        for (var i = 0; i < 50 && !File.Exists(path); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(File.Exists(path), $"没有找到插件调用日志: {path}");
        _logDir = Path.GetDirectoryName(path);
        return File.ReadAllLines(path)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    private void InstallPlugin(string root, string mockDir)
    {
        _installedRoots.Add(root);
        var dir = Path.Combine(root, "plugins", PluginId);
        TryDelete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(mockDir, "mock_game_module.dll"), Path.Combine(dir, "module.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{PluginId}}", "displayName": "Mock", "abiMajor": 1, "exeNames": ["Mock.exe"], "caps": ["hotParams"], "settingsSchema": {{SettingsSchema}} }""");
        TryDelete(Path.Combine(root, "plugin-data", PluginId), recursive: true);

        var trust = new PluginTrustStore(Path.Combine(root, "plugin-trust.json"));
        var entry = new PluginCatalog(Path.Combine(root, "plugins"), trust).Scan().Single(item => item.Id == PluginId);
        trust.Confirm(new PluginTrustRecord(PluginId, entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow));
    }

    private bool IsOurFakeGame(Process process)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, Path.Combine(_fakeGameDir!, "Mock.exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private SparxieBroker.SparxieBrokerClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_brokerPipe).ConnectAsync(ct),
        };
        return new SparxieBroker.SparxieBrokerClient(GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler }));
    }

    private static string Describe(List<SessionEvent> events)
    {
        lock (events)
        {
            return string.Join(" → ", events.Select(e => $"{e.State}({e.Message})"));
        }
    }

    /// <summary>与 AppDataDirectory.Resolve 一致：发布包里 exe 在 app-<版本> 目录下，数据根是它的上一级；开发构建里就是 exe 所在目录。</summary>
    private static string DataRootOf(string exeDirectory)
    {
        var trimmed = exeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed).StartsWith("app-", StringComparison.OrdinalIgnoreCase) && Directory.GetParent(trimmed) is { } parent
            ? parent.FullName
            : trimmed;
    }

    private static string? MockDir()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var root = RepoRoot();
        var dir = Path.Combine(root, "tests", "MockGameModule", "bin");
        return File.Exists(Path.Combine(dir, "mock_game_module.dll")) && File.Exists(Path.Combine(dir, "Mock.exe")) ? dir : null;
    }

    private static string Locate(string project)
    {
        // 设置 SPARXIE_SMOKE_APP_DIR 指向解压后的发布包 app-<版本> 目录，就能对打出来的包跑同一套冒烟。
        if (Environment.GetEnvironmentVariable("SPARXIE_SMOKE_APP_DIR") is { Length: > 0 } appDir)
        {
            var packaged = Path.Combine(appDir, project + ".exe");
            Assert.True(File.Exists(packaged), $"未找到 {packaged}");
            return packaged;
        }

        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
            ?? throw new InvalidOperationException("Test output does not have a configuration directory.");
        var path = Path.Combine(RepoRoot(), "src", project, "bin", configuration, "net10.0-windows", project + ".exe");
        Assert.True(File.Exists(path), $"未找到 {path}");
        return path;
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sparxie.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结论
        }
    }

    private static void TryDelete(string? path, bool recursive)
    {
        try
        {
            if (path is null)
            {
                return;
            }

            if (recursive && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (!recursive && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结论
        }
    }
}
