using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Rpc;

namespace Broker.Tests;

/// <summary>
/// 端到端：真实 Broker + 真实 SessionHost + <b>真实的终末地插件包</b>（环境变量 ENDFIELD_PLUGIN_DIR 指向含 module.dll 与 plugin.json 的
/// ext.skyhua0224.endfield 目录；未设置时直接返回）。
/// 游戏用 notepad.exe 的副本冒充：它没有内嵌的厂商签名，生产路径（永远校验签名）必须在假父进程这一步拒绝它，
/// 所以验证的是"从 Broker 校验、插件加载、设置归一化、运行库解压、ini 写入，一直到安全失败"的整条链路，而不是游戏真的被注入。
/// 做法：把插件放进 Broker 与 SessionHost 各自的数据目录（它们所在的 bin 目录，或发布包里 app-* 的上一级），结束后清理。
/// </summary>
[Collection("SessionHostProcess")]
public sealed class EndfieldPluginEndToEndTests : IAsyncLifetime
{
    private const string PluginId = "ext.skyhua0224.endfield";
    private readonly string _brokerPipe = $"sparxie-endfield-e2e-{Guid.NewGuid():N}";
    private readonly List<string> _roots = [];
    private Process? _broker;
    private string? _gameDir;
    private bool _skipped;

    private static string? PluginDirectory => Environment.GetEnvironmentVariable("ENDFIELD_PLUGIN_DIR");

    public Task InitializeAsync()
    {
        if (PluginDirectory is not { Length: > 0 } source || !File.Exists(Path.Combine(source, "module.dll")))
        {
            _skipped = true;
            return Task.CompletedTask;
        }

        var brokerExe = Locate("Sparxie.Broker");
        var hostExe = Locate("Sparxie.SessionHost");
        foreach (var exe in new[] { brokerExe, hostExe })
        {
            Install(DataRootOf(Path.GetDirectoryName(exe)!), source);
        }

        _gameDir = Path.Combine(Path.GetTempPath(), "sparxie-endfield-e2e-game", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_gameDir);
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        File.Copy(notepad, Path.Combine(_gameDir, "Endfield.exe"));
        File.Copy(notepad, Path.Combine(_gameDir, "PlatformProcess.exe")); // 同一个签名者，通过"签名者一致"检查；但两者都没有内嵌签名

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
        await Task.Delay(500);
        foreach (var root in _roots)
        {
            TryDelete(Path.Combine(root, "plugins", PluginId));
            TryDelete(Path.Combine(root, "plugin-data", PluginId));
            TryDelete(Path.Combine(root, "plugin-trust.json"));
            TryDeleteEmpty(Path.Combine(root, "plugins"));
            TryDeleteEmpty(Path.Combine(root, "plugin-data"));
        }

        TryDelete(_gameDir);
    }

    [Fact]
    public async Task 真实插件包_Broker放行_插件加载_设置归一化写入ini_无厂商签名的游戏被安全拒绝()
    {
        if (_skipped) { return; }

        var client = CreateClient();
        var events = new List<SessionEvent>();
        var failed = new TaskCompletionSource<SessionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var stream = Task.Run(async () =>
        {
            try
            {
                var call = client.StreamEvents(new StreamEventsRequest { ProtocolVersion = RpcContract.ProtocolVersion, RequestId = Guid.NewGuid().ToString("N") }, cancellationToken: cts.Token);
                await foreach (var ev in call.ResponseStream.ReadAllAsync(cts.Token))
                {
                    lock (events) { events.Add(ev); }

                    if (ev.State is "Failed") { failed.TrySetResult(ev); }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or RpcException)
            {
                // 结束时取消
            }
        });

        var response = await StartAsync(client, Path.Combine(_gameDir!, "Endfield.exe"),
            """{ "uiScaleMode": "manual", "uiScalePercent": 133, "safeAreaMode": "manual", "safeLeft": 186, "safeRight": 9999, "width": 99999, "inputMode": "gamepad", "evil": 1 }""");
        Assert.True(response.Accepted, response.Message); // Broker：EXE 名在 manifest 白名单、settings_json 结构合法、插件已确认

        var last = await failed.Task.WaitAsync(TimeSpan.FromSeconds(90));
        cts.Cancel();
        await stream;

        string states;
        lock (events) { states = string.Join(" → ", events.Select(e => e.State)); }
        Assert.DoesNotContain(events, e => e.State == "Running"); // 没有任何一步让游戏被当成已运行
        Assert.Contains("数字签名无效", last.Message);             // 失败的原因是生产路径的签名校验，而不是别的故障：事件序列 " + states

        // 插件确实被加载并跑到了假父进程这一步：运行库已解压、设置已按 schema 归一化写进 ini
        var hostRoot = _roots.Last();
        var ini = Directory.GetFiles(Path.Combine(hostRoot, "plugin-data", PluginId), "endfield-ui.ini", SearchOption.AllDirectories).Single();
        Assert.Equal("ui_scale=1.35\r\nsafe_area=186,600\r\n", await File.ReadAllTextAsync(ini));
        Assert.Single(Directory.GetFiles(Path.Combine(hostRoot, "plugin-data", PluginId), "EndfieldRuntime.dll", SearchOption.AllDirectories));

        // 安全失败：没有残留的假父进程，游戏目录里没有暂存目录
        Assert.Empty(Process.GetProcessesByName("Games"));
        Assert.Empty(Directory.GetDirectories(_gameDir!));
        _ = states;
    }

    [Fact]
    public async Task 插件未确认时Broker拒绝_不加载DLL也不解压运行库()
    {
        if (_skipped) { return; }

        foreach (var root in _roots)
        {
            new PluginTrustStore(Path.Combine(root, "plugin-trust.json")).Remove(PluginId);
        }

        var response = await StartAsync(CreateClient(), Path.Combine(_gameDir!, "Endfield.exe"), "{}");

        Assert.False(response.Accepted);
        Assert.False(Directory.Exists(Path.Combine(_roots.Last(), "plugin-data", PluginId)));
    }

    [Fact]
    public async Task EXE名不在manifest白名单时Broker拒绝()
    {
        if (_skipped) { return; }

        File.Copy(Path.Combine(_gameDir!, "Endfield.exe"), Path.Combine(_gameDir!, "Other.exe"));

        var response = await StartAsync(CreateClient(), Path.Combine(_gameDir!, "Other.exe"), "{}");

        Assert.False(response.Accepted);
    }

    private async Task<StartSessionResponse> StartAsync(SparxieBroker.SparxieBrokerClient client, string exe, string settingsJson)
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
                    Profile = new ProfileSnapshot { ProfileId = "p1", DisplayName = "终末地", Game = PluginId, ExecutablePath = exe, SettingsJson = settingsJson },
                }, cancellationToken: cts.Token);
            }
            catch (Exception ex) when (ex is RpcException or HttpRequestException or IOException)
            {
                last = ex;
                await Task.Delay(250);
            }
        }

        throw new TimeoutException($"Broker 30 秒内未就绪: {last?.Message}");
    }

    private void Install(string root, string source)
    {
        _roots.Add(root);
        var dir = Path.Combine(root, "plugins", PluginId);
        TryDelete(dir);
        Directory.CreateDirectory(dir);
        foreach (var file in new[] { "module.dll", "plugin.json" })
        {
            File.Copy(Path.Combine(source, file), Path.Combine(dir, file));
        }

        TryDelete(Path.Combine(root, "plugin-data", PluginId));
        var trust = new PluginTrustStore(Path.Combine(root, "plugin-trust.json"));
        var entry = new PluginCatalog(Path.Combine(root, "plugins"), trust).Scan().Single(item => item.Id == PluginId);
        Assert.NotEqual(PluginAvailability.Invalid, entry.Availability);
        trust.Confirm(new PluginTrustRecord(PluginId, entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow));
    }

    private SparxieBroker.SparxieBrokerClient CreateClient()
    {
        var handler = new SocketsHttpHandler { ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(_brokerPipe).ConnectAsync(ct) };
        return new SparxieBroker.SparxieBrokerClient(GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler }));
    }

    /// <summary>与 AppDataDirectory.Resolve 一致：发布包里 exe 在 app-&lt;版本&gt; 下，数据根是上一级；开发构建里就是 exe 所在目录。</summary>
    private static string DataRootOf(string exeDirectory)
    {
        var trimmed = exeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed).StartsWith("app-", StringComparison.OrdinalIgnoreCase) && Directory.GetParent(trimmed) is { } parent ? parent.FullName : trimmed;
    }

    private static string Locate(string project)
    {
        if (Environment.GetEnvironmentVariable("SPARXIE_SMOKE_APP_DIR") is { Length: > 0 } appDir)
        {
            var packaged = Path.Combine(appDir, project + ".exe");
            Assert.True(File.Exists(packaged), $"未找到 {packaged}");
            return packaged;
        }

        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Name
            ?? throw new InvalidOperationException("Test output does not have a configuration directory.");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sparxie.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "src", project, "bin", configuration, "net10.0-windows", project + ".exe");
        Assert.True(File.Exists(path), $"未找到 {path}");
        return path;
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is null) { return; }

            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            else if (File.Exists(path)) { File.Delete(path); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结论
        }
    }

    private static void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) { Directory.Delete(path); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结论
        }
    }
}
