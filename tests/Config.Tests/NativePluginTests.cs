using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>用 tests/MockGameModule 构建出的 mock DLL 验证加载、交叉核对和会话线程规则。未构建 mock 时直接返回（见 build_mock.ps1）。</summary>
public sealed class NativePluginTests : IDisposable
{
    private const string Id = "ext.mock.game";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-native-plugin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 加载过的 DLL 在进程内无法删除，清理失败不影响结论
        }
    }

    private static string? FindMockDll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "MockGameModule", "bin", "mock_game_module.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private (PluginCatalog Catalog, PluginEntry Entry, string DataDir)? Install(string? exe = null, string[]? caps = null)
    {
        var dll = FindMockDll();
        if (dll is null)
        {
            return null;
        }

        var plugins = Path.Combine(_root, "plugins");
        var dir = Path.Combine(plugins, Id);
        Directory.CreateDirectory(dir);
        File.Copy(dll, Path.Combine(dir, "module.dll"));
        var capsJson = string.Join(",", (caps ?? ["hotParams"]).Select(c => $"\"{c}\""));
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{Id}}", "displayName": "Mock", "abiMajor": 1, "exeNames": ["{{exe ?? "Mock.exe"}}"], "caps": [{{capsJson}}] }""");

        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var catalog = new PluginCatalog(plugins, trust);
        var entry = catalog.Scan().Single();
        trust.Confirm(new PluginTrustRecord(entry.Id, entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow));
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDir);
        return (catalog, catalog.Scan().Single(), dataDir);
    }

    [Fact]
    public async Task 完整会话的所有调用在同一专用线程上()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "{}", dataDir, dataDir, Timeout);
        await session.PrepareLaunchAsync(Timeout);
        await session.AttachJobAsync(new IntPtr(1), Timeout);
        await session.InstallAsync(0, Timeout);
        await session.PostInstallAsync(Timeout);
        await session.SetParamAsync("targetFps", 60, Timeout);
        Assert.False(await session.WaitExitAsync(10, Timeout));
        Assert.True(await session.WaitExitAsync(0, Timeout));
        session.Dispose();
        session.Dispose(); // 幂等

        var calls = File.ReadAllLines(Path.Combine(dataDir, "calls.log")).Select(line => line.Split(' ')).ToList();
        Assert.Equal(
            ["create_session", "prepare_launch", "attach_job", "install", "post_install", "set_param", "wait_exit", "wait_exit", "release"],
            calls.Select(parts => parts[0]));
        Assert.Single(calls.Select(parts => parts[1]).Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId.ToString(), calls[0][1]);
    }

    [Fact]
    public async Task 插件返回错误时抛出带清理后消息的异常()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);
        await session.CreateAsync(@"D:\G\Mock.exe", "", "{\"mode\":\"fail-install\"}", dataDir, dataDir, Timeout);
        await session.PrepareLaunchAsync(Timeout);

        var ex = await Assert.ThrowsAsync<PluginCallException>(() => session.InstallAsync(0, Timeout));
        Assert.Equal(100, ex.Code);
        Assert.Contains("mock inject failed", ex.Message);
        Assert.Equal(3u, ex.Stage);
        Assert.Equal(7u, ex.Detail);
        Assert.Contains("阶段 3", ex.Message);
    }

    [Fact]
    public async Task 错误信息里的控制字符被过滤()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);
        await session.CreateAsync(@"D:\G\Mock.exe", "", "bad-result", dataDir, dataDir, Timeout);

        var ex = await Assert.ThrowsAsync<PluginCallException>(() => session.PrepareLaunchAsync(Timeout));
        Assert.DoesNotContain(ex.Message, c => char.IsControl(c));
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public async Task 调用超时后抛出TimeoutException()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);
        await session.CreateAsync(@"D:\G\Mock.exe", "", "slow-install", dataDir, dataDir, Timeout);
        await session.PrepareLaunchAsync(Timeout);

        await Assert.ThrowsAsync<TimeoutException>(() => session.InstallAsync(0, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void manifest与get_info的EXE列表不一致时拒绝加载()
    {
        if (Install(exe: "Other.exe") is not var (_, entry, _)) { return; }

        var ex = Assert.Throws<PluginLoadException>(() => NativePluginLibrary.Load(entry));
        Assert.Contains("EXE", ex.Message);
    }

    [Fact]
    public void manifest与get_info的能力位不一致时拒绝加载()
    {
        if (Install(caps: ["injects"]) is not var (_, entry, _)) { return; }

        var ex = Assert.Throws<PluginLoadException>(() => NativePluginLibrary.Load(entry));
        Assert.Contains("能力", ex.Message);
    }

    [Fact]
    public void 未信任的插件拒绝加载()
    {
        if (Install() is not var (catalog, _, _)) { return; }

        new PluginTrustStore(Path.Combine(_root, "plugin-trust.json")).Remove(Id);
        var entry = catalog.Scan().Single();

        Assert.Equal(PluginAvailability.Untrusted, entry.Availability);
        Assert.Throws<PluginLoadException>(() => NativePluginLibrary.Load(entry));
    }

    [Fact]
    public void 加载后DLL无法被改写()
    {
        if (Install() is not var (_, entry, _)) { return; }

        using var library = NativePluginLibrary.Load(entry);

        Assert.ThrowsAny<IOException>(() => File.WriteAllText(Path.Combine(entry.Directory, "module.dll"), "tamper"));
    }

    private sealed class MemoryStore : IRegistryHookStore
    {
        public Dictionary<(string, string), RegistryHookValue> Values { get; } = new();

        public RegistryHookValue? Read(string key, string name) => Values.GetValueOrDefault((key.ToLowerInvariant(), name.ToLowerInvariant()));

        public void Write(string key, string name, RegistryHookValue value) => Values[(key.ToLowerInvariant(), name.ToLowerInvariant())] = value;

        public void Delete(string key, string name) => Values.Remove((key.ToLowerInvariant(), name.ToLowerInvariant()));
    }

    private const string HookKey = @"Software\SparxieMock\Game";

    [Fact]
    public async Task 插件通过宿主回调改键_宿主备份并在恢复时还原()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        var store = new MemoryStore();
        store.Values[(HookKey.ToLowerInvariant(), "mockvalue")] = new RegistryHookValue(RegistryHookValue.Dword, "3");
        var hooks = new RegistryHookSession(Id, [HookKey], store, Path.Combine(_root, "hook-backup.json"));
        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "reg-set", dataDir, dataDir, Timeout, hooks);
        await session.PrepareLaunchAsync(Timeout);

        Assert.Equal("7", store.Read(HookKey, "MockValue")!.Data); // 插件改成了 7
        Assert.Contains("reg_set 0", File.ReadAllText(Path.Combine(dataDir, "calls.log")));

        Assert.True(hooks.Restore());
        Assert.Equal("3", store.Read(HookKey, "MockValue")!.Data);
    }

    [Fact]
    public async Task 插件越过声明前缀改键被宿主拒绝()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        var store = new MemoryStore();
        var hooks = new RegistryHookSession(Id, [HookKey], store, Path.Combine(_root, "hook-backup.json"));
        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "reg-bad", dataDir, dataDir, Timeout, hooks);
        await session.PrepareLaunchAsync(Timeout);

        Assert.Empty(store.Values);
        Assert.Contains("reg_bad 1", File.ReadAllText(Path.Combine(dataDir, "calls.log")));
    }

    [Fact]
    public async Task 插件删除值同样被备份并可恢复()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        var store = new MemoryStore();
        store.Values[(HookKey.ToLowerInvariant(), "mockvalue")] = new RegistryHookValue(RegistryHookValue.Dword, "3");
        var hooks = new RegistryHookSession(Id, [HookKey], store, Path.Combine(_root, "hook-backup.json"));
        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "reg-delete", dataDir, dataDir, Timeout, hooks);
        await session.PrepareLaunchAsync(Timeout);
        Assert.Null(store.Read(HookKey, "MockValue"));

        Assert.True(hooks.Restore());
        Assert.Equal("3", store.Read(HookKey, "MockValue")!.Data);
    }

    [Fact]
    public async Task 没有传入钩子时插件拿不到宿主服务()
    {
        if (Install() is not var (_, entry, dataDir)) { return; }

        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "reg-set", dataDir, dataDir, Timeout);
        await session.PrepareLaunchAsync(Timeout);

        Assert.Contains("no_host", File.ReadAllText(Path.Combine(dataDir, "calls.log")));
    }
}
