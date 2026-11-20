using System.Text.Json;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>审查发现的注册表钩子问题的回归测试：落盘失败不留"已备份"假象、部分恢复后重写备份。</summary>
public sealed class RegistryHookRegressionTests : IDisposable
{
    private const string Prefix = @"Software\Vendor\Game";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-reghook-regress", Guid.NewGuid().ToString("N"));
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

    private sealed class Store : IRegistryHookStore
    {
        public Dictionary<(string, string), RegistryHookValue> Values { get; } = new();

        public bool FailWrites { get; set; }

        public int WriteCount { get; private set; }

        public RegistryHookValue? Read(string key, string name) => Values.GetValueOrDefault((key.ToLowerInvariant(), name.ToLowerInvariant()));

        public void Write(string key, string name, RegistryHookValue value)
        {
            WriteCount++;
            if (FailWrites)
            {
                throw new UnauthorizedAccessException("denied");
            }

            Values[(key.ToLowerInvariant(), name.ToLowerInvariant())] = value;
        }

        public void Delete(string key, string name) => Values.Remove((key.ToLowerInvariant(), name.ToLowerInvariant()));
    }

    [Fact]
    public void 备份落盘失败时不写注册表_重试也不会跳过备份()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "recovery");
        File.WriteAllText(blocker, "我是个文件，挡住了 recovery 目录"); // 让 CreateDirectory 失败
        var store = new Store();
        var session = new RegistryHookSession("ext.acme.demo", [Prefix], store, BackupPath);

        var first = session.SetDword(Prefix, "v", 1);
        var second = session.SetDword(Prefix, "v", 2); // 以前这里会因为"已记录"而跳过备份，直接写入

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(0, store.WriteCount);
        Assert.Empty(store.Values);
        Assert.False(session.HasPendingBackup);
    }

    [Fact]
    public void 备份目录恢复可写后同一个值可以正常备份并写入()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "recovery");
        File.WriteAllText(blocker, "x");
        var store = new Store();
        store.Values[(Prefix.ToLowerInvariant(), "v")] = new RegistryHookValue(RegistryHookValue.Dword, "5");
        var session = new RegistryHookSession("ext.acme.demo", [Prefix], store, BackupPath);
        Assert.False(session.SetDword(Prefix, "v", 1).Ok);

        File.Delete(blocker);

        Assert.True(session.SetDword(Prefix, "v", 1).Ok);
        Assert.True(File.Exists(BackupPath));
        Assert.True(session.Restore());
        Assert.Equal("5", store.Read(Prefix, "v")!.Data);
    }

    [Fact]
    public void 部分恢复失败后备份只保留没恢复的条目()
    {
        var store = new Store();
        store.Values[(Prefix.ToLowerInvariant(), "a")] = new RegistryHookValue(RegistryHookValue.Dword, "1"); // 原本存在：恢复要 Write
        var session = new RegistryHookSession("ext.acme.demo", [Prefix], store, BackupPath);
        session.SetDword(Prefix, "a", 10);
        session.SetDword(Prefix, "b", 20); // 原本不存在：恢复只要 Delete
        store.FailWrites = true;

        Assert.False(session.Restore());

        Assert.Null(store.Read(Prefix, "b")); // b 已经恢复（删除）
        using var document = JsonDocument.Parse(File.ReadAllText(BackupPath));
        var names = document.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(["a"], names); // 备份里不再有 b，避免下次重放

        store.FailWrites = false;
        Assert.True(session.Restore());
        Assert.Equal("1", store.Read(Prefix, "a")!.Data);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void 遗留备份部分恢复后重写备份文件()
    {
        var store = new Store();
        store.Values[(Prefix.ToLowerInvariant(), "a")] = new RegistryHookValue(RegistryHookValue.Dword, "10");
        store.Values[(Prefix.ToLowerInvariant(), "b")] = new RegistryHookValue(RegistryHookValue.Dword, "20");
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, """
            { "pluginId": "ext.acme.demo", "prefixes": ["Software\\Vendor\\Game"],
              "entries": [
                { "key": "Software\\Vendor\\Game", "name": "a", "existed": true, "kind": "dword", "data": "1" },
                { "key": "Software\\Vendor\\Game", "name": "b", "existed": false, "kind": null, "data": null } ] }
            """);
        store.FailWrites = true; // a 要 Write（失败），b 要 Delete（成功）

        Assert.False(RegistryHookSession.RestoreLeftover(BackupPath, store));

        Assert.Null(store.Read(Prefix, "b"));
        using var document = JsonDocument.Parse(File.ReadAllText(BackupPath));
        Assert.Equal(["a"], document.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList());

        store.FailWrites = false;
        Assert.True(RegistryHookSession.RestoreLeftover(BackupPath, store));
        Assert.Equal("1", store.Read(Prefix, "a")!.Data);
        Assert.False(File.Exists(BackupPath));
    }
}
