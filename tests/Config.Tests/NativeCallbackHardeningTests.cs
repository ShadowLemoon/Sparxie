using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>用真实 mock DLL 验证审查发现的回调问题：超长字符串、NULL result、超时并 Dispose 之后的迟到回调。未构建 mock 时直接返回。</summary>
public sealed class NativeCallbackHardeningTests : IDisposable
{
    private const string Id = "ext.mock.game";
    private const string HookKey = @"Software\SparxieMock\Game";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-native-hardening", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 已加载的 DLL 在进程内无法删除，清理失败不影响结论
        }
    }

    private sealed class MemoryStore : IRegistryHookStore
    {
        private readonly object _gate = new();

        public Dictionary<(string, string), RegistryHookValue> Values { get; } = new();

        public RegistryHookValue? Read(string key, string name)
        {
            lock (_gate)
            {
                return Values.GetValueOrDefault((key.ToLowerInvariant(), name.ToLowerInvariant()));
            }
        }

        public void Write(string key, string name, RegistryHookValue value)
        {
            lock (_gate)
            {
                Values[(key.ToLowerInvariant(), name.ToLowerInvariant())] = value;
            }
        }

        public void Delete(string key, string name)
        {
            lock (_gate)
            {
                Values.Remove((key.ToLowerInvariant(), name.ToLowerInvariant()));
            }
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

    private (PluginEntry Entry, string DataDir)? Install()
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
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{Id}}", "displayName": "Mock", "abiMajor": 1, "exeNames": ["Mock.exe"], "caps": ["hotParams"] }""");
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var catalog = new PluginCatalog(plugins, trust);
        var scanned = catalog.Scan().Single();
        trust.Confirm(new PluginTrustRecord(Id, scanned.DllSha256!, scanned.ManifestSha256!, scanned.DllSize, DateTimeOffset.UtcNow));
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDir);
        return (catalog.Scan().Single(), dataDir);
    }

    private RegistryHookSession Hooks(MemoryStore store) => new(Id, [HookKey], store, Path.Combine(_root, "hook-backup.json"));

    [Fact]
    public async Task 超过上限的字符串被拒绝_不会被截成空串写入()
    {
        if (Install() is not var (entry, dataDir)) { return; }

        var store = new MemoryStore();
        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "reg-long", dataDir, dataDir, Timeout, Hooks(store));
        await session.PrepareLaunchAsync(Timeout);

        Assert.Empty(store.Values); // 5000 字符 > 4096：以前会被读成空串并写入
        Assert.Contains("reg_long 1", File.ReadAllText(Path.Combine(dataDir, "calls.log")));
    }

    [Fact]
    public async Task 插件传NULL的result指针时宿主不崩溃()
    {
        if (Install() is not var (entry, dataDir)) { return; }

        var store = new MemoryStore();
        using var library = NativePluginLibrary.Load(entry);
        using var session = new NativePluginSession(library);

        await session.CreateAsync(@"D:\G\Mock.exe", "", "reg-nullres", dataDir, dataDir, Timeout, Hooks(store));
        await session.PrepareLaunchAsync(Timeout);

        Assert.Empty(store.Values);
        Assert.Contains("reg_nullres 1", File.ReadAllText(Path.Combine(dataDir, "calls.log")));
    }

    [Fact]
    public async Task 调用超时并Dispose之后插件迟到的回调仍然安全()
    {
        if (Install() is not var (entry, dataDir)) { return; }

        var store = new MemoryStore();
        var hooks = Hooks(store);
        using var library = NativePluginLibrary.Load(entry);
        var session = new NativePluginSession(library);
        await session.CreateAsync(@"D:\G\Mock.exe", "", "slow-reg", dataDir, dataDir, Timeout, hooks);
        await session.PrepareLaunchAsync(Timeout);

        // install 在插件里睡 6 秒后才回调宿主；宿主 200ms 就超时，随后 Dispose（等 5 秒仍不返回，转为有意泄漏回调表）。
        await Assert.ThrowsAsync<TimeoutException>(() => session.InstallAsync(0, TimeSpan.FromMilliseconds(200)));
        session.Dispose();

        var log = Path.Combine(dataDir, "calls.log");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !File.ReadAllText(log).Contains("reg_late"))
        {
            await Task.Delay(100);
        }

        Assert.Contains("reg_late 0", File.ReadAllText(log)); // 迟到的回调成功执行，回调表没有被提前释放
        Assert.Equal("9", store.Read(HookKey, "LateValue")!.Data);
        Assert.True(hooks.Restore());
        Assert.Null(store.Read(HookKey, "LateValue"));
    }

    [Fact]
    public async Task create_session超时后Dispose仍会在创建完成后释放会话()
    {
        if (Install() is not var (entry, dataDir)) { return; }

        using var library = NativePluginLibrary.Load(entry);
        var session = new NativePluginSession(library);
        // 让 create_session 自己很快返回，但验证"Dispose 总会排队 release"：直接 Dispose 不应遗漏已创建的会话。
        await session.CreateAsync(@"D:\G\Mock.exe", "", "{}", dataDir, dataDir, Timeout);
        session.Dispose();

        Assert.Contains("release", File.ReadAllText(Path.Combine(dataDir, "calls.log")));
    }
}
