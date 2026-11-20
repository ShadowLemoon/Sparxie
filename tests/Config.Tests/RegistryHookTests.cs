using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>注册表键备份/恢复钩子：与画面设置无关，全部用内存假注册表验证。</summary>
public sealed class RegistryHookTests : IDisposable
{
    private const string Prefix = @"Software\Vendor\Game";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-reghook-tests", Guid.NewGuid().ToString("N"));
    private string BackupPath => Path.Combine(_dir, "recovery", "registry-hook-ext.acme.demo.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private sealed class FakeStore : IRegistryHookStore
    {
        public Dictionary<(string Key, string Name), RegistryHookValue> Values { get; } = new();

        public bool FailWrites { get; set; }

        public RegistryHookValue? Read(string key, string name) =>
            Values.TryGetValue((key.ToLowerInvariant(), name.ToLowerInvariant()), out var value) ? value : null;

        public void Write(string key, string name, RegistryHookValue value)
        {
            if (FailWrites)
            {
                throw new UnauthorizedAccessException("denied");
            }

            Values[(key.ToLowerInvariant(), name.ToLowerInvariant())] = value;
        }

        public void Delete(string key, string name) => Values.Remove((key.ToLowerInvariant(), name.ToLowerInvariant()));
    }

    private RegistryHookSession Session(FakeStore store, params string[] prefixes) =>
        new("ext.acme.demo", prefixes.Length == 0 ? [Prefix] : prefixes, store, BackupPath);

    [Theory]
    [InlineData(@"Software\Vendor\Game", true)]
    [InlineData(@"Software\Vendor\Game\Sub", true)]
    [InlineData(@"software\vendor\game", true)]
    [InlineData(@"Software\Vendor", false)]
    [InlineData(@"Software", false)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Vendor\Game", false)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion", false)]
    [InlineData(@"Software\Vendor\Run", false)]
    [InlineData(@"Software\Vendor\Policies\X", false)]
    [InlineData(@"Software\Vendor\..\Game", false)]
    [InlineData(@"Software\\Vendor\Game", false)]
    [InlineData(@"\Software\Vendor\Game", false)]
    [InlineData(@"Software\Vendor\Ga:me", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 声明前缀的合法性(string? prefix, bool valid)
    {
        Assert.Equal(valid, RegistryHookSession.IsValidPrefix(prefix));
    }

    [Fact]
    public void 改键前先落盘原值_恢复后还原并删除备份()
    {
        var store = new FakeStore();
        store.Values[(Prefix.ToLowerInvariant(), "volume")] = new RegistryHookValue(RegistryHookValue.Dword, "5");
        var session = Session(store);

        Assert.True(session.SetDword(Prefix, "Volume", 9).Ok);

        Assert.True(File.Exists(BackupPath)); // 已落盘
        Assert.Equal("9", store.Read(Prefix, "Volume")!.Data);
        Assert.True(session.Restore());
        Assert.Equal("5", store.Read(Prefix, "Volume")!.Data);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void 原本不存在的值恢复时被删除()
    {
        var store = new FakeStore();
        var session = Session(store);

        Assert.True(session.SetString(Prefix + @"\Sub", "Name", "hello").Ok);
        Assert.NotNull(store.Read(Prefix + @"\Sub", "Name"));

        Assert.True(session.Restore());
        Assert.Null(store.Read(Prefix + @"\Sub", "Name"));
    }

    [Fact]
    public void 删除值也会备份并可恢复()
    {
        var store = new FakeStore();
        store.Values[(Prefix.ToLowerInvariant(), "token")] = new RegistryHookValue(RegistryHookValue.String, "abc");
        var session = Session(store);

        Assert.True(session.DeleteValue(Prefix, "Token").Ok);
        Assert.Null(store.Read(Prefix, "Token"));

        Assert.True(session.Restore());
        Assert.Equal("abc", store.Read(Prefix, "Token")!.Data);
    }

    [Fact]
    public void 同一个值多次改动只备份最初的原值()
    {
        var store = new FakeStore();
        store.Values[(Prefix.ToLowerInvariant(), "v")] = new RegistryHookValue(RegistryHookValue.Dword, "1");
        var session = Session(store);

        session.SetDword(Prefix, "v", 2);
        session.SetDword(Prefix, "v", 3);
        session.Restore();

        Assert.Equal("1", store.Read(Prefix, "v")!.Data);
    }

    [Theory]
    [InlineData(@"Software\Vendor\GameX")] // 前缀边界：不是子键
    [InlineData(@"Software\Vendor")]
    [InlineData(@"Software\Other\Game")]
    [InlineData(@"Software\Vendor\Game\..\Other")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run")]
    public void 前缀之外的键被拒绝且不写入(string key)
    {
        var store = new FakeStore();
        var session = Session(store);

        var result = session.SetDword(key, "v", 1);

        Assert.False(result.Ok);
        Assert.Empty(store.Values);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void 前缀匹配忽略大小写()
    {
        var store = new FakeStore();

        Assert.True(Session(store).SetDword(@"SOFTWARE\VENDOR\GAME\x", "v", 1).Ok);
    }

    [Fact]
    public void 非法值名被拒绝()
    {
        var session = Session(new FakeStore());

        Assert.False(session.SetDword(Prefix, "", 1).Ok);
        Assert.False(session.SetDword(Prefix, @"a\b", 1).Ok);
    }

    [Fact]
    public void 写入失败时返回失败而不抛异常()
    {
        var store = new FakeStore { FailWrites = true };

        var result = Session(store).SetDword(Prefix, "v", 1);

        Assert.False(result.Ok);
    }

    [Fact]
    public void 异常退出遗留的备份可在没有插件的情况下恢复()
    {
        var store = new FakeStore();
        store.Values[(Prefix.ToLowerInvariant(), "v")] = new RegistryHookValue(RegistryHookValue.Dword, "1");
        var crashed = Session(store);
        crashed.SetDword(Prefix, "v", 2); // 模拟会话中途崩溃：不调用 Restore

        Assert.True(RegistryHookSession.RestoreLeftover(BackupPath, store));

        Assert.Equal("1", store.Read(Prefix, "v")!.Data);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void 被篡改的备份不能写入前缀之外的键()
    {
        var store = new FakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, """
            { "pluginId": "ext.acme.demo", "prefixes": ["Software\\Vendor\\Game"],
              "entries": [ { "key": "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "name": "evil", "existed": true, "kind": "string", "data": "calc.exe" } ] }
            """);

        var ok = RegistryHookSession.RestoreLeftover(BackupPath, store);

        Assert.False(ok);
        Assert.Empty(store.Values);
        Assert.True(File.Exists(BackupPath)); // 留给人工处理
    }

    [Fact]
    public void 无法解析的备份不删除()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, "{ not json");

        Assert.False(RegistryHookSession.RestoreLeftover(BackupPath, new FakeStore()));
        Assert.True(File.Exists(BackupPath));
    }

    [Fact]
    public void 没有遗留备份时直接成功()
    {
        Assert.True(RegistryHookSession.RestoreLeftover(BackupPath, new FakeStore()));
    }

    [Fact]
    public void 恢复失败时保留备份以便下次重试()
    {
        var store = new FakeStore();
        store.Values[(Prefix.ToLowerInvariant(), "k")] = new RegistryHookValue(RegistryHookValue.Dword, "1");
        var session = Session(store);
        session.SetDword(Prefix, "k", 5);
        store.FailWrites = true; // 恢复原值要走 Write，让它失败

        Assert.False(session.Restore());
        Assert.True(File.Exists(BackupPath));

        store.FailWrites = false;
        Assert.True(session.Restore());
        Assert.Equal("1", store.Read(Prefix, "k")!.Data);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void manifest里非法的registryHooks前缀使插件无效()
    {
        var json = """{ "id": "ext.acme.demo", "displayName": "x", "abiMajor": 1, "exeNames": ["D.exe"], "registryHooks": { "keys": ["Software\\Microsoft\\Windows\\CurrentVersion\\Run"] } }""";

        Assert.False(PluginManifest.TryParse(System.Text.Encoding.UTF8.GetBytes(json), "ext.acme.demo", out _, out var error));
        Assert.Contains("registryHooks", error);
    }

    [Fact]
    public void manifest里合法的registryHooks可解析()
    {
        var json = """{ "id": "ext.acme.demo", "displayName": "x", "abiMajor": 1, "exeNames": ["D.exe"], "registryHooks": { "keys": ["Software\\Vendor\\Game"] } }""";

        Assert.True(PluginManifest.TryParse(System.Text.Encoding.UTF8.GetBytes(json), "ext.acme.demo", out var manifest, out _));
        Assert.Equal([@"Software\Vendor\Game"], manifest.RegistryHooks!.Keys);
    }
}
