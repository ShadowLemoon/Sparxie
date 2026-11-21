using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

public sealed class PluginsViewModelTests : IDisposable
{
    private const string Id = "ext.acme.demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-plugins-vm-tests", Guid.NewGuid().ToString("N"));
    private string PluginDir => Path.Combine(_root, "plugins", Id);

    public PluginsViewModelTests()
    {
        Directory.CreateDirectory(PluginDir);
        File.WriteAllText(Path.Combine(PluginDir, "plugin.json"),
            """{ "id": "ext.acme.demo", "displayName": "演示", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": ["injects"], "license": "MIT" }""");
        File.WriteAllText(Path.Combine(PluginDir, "module.dll"), "dll-v1");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private (PluginsViewModel Vm, PluginTrustStore Trust, List<string> Removed) Create()
    {
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var catalog = new PluginCatalog(Path.Combine(_root, "plugins"), trust);
        var removed = new List<string>();
        return (new PluginsViewModel(catalog, trust, _root, removed.Add, _registry), trust, removed);
    }

    private sealed class FakeRegistry : IRegistryHookStore
    {
        public Dictionary<(string, string), RegistryHookValue> Values { get; } = new();

        public bool FailWrites { get; set; }

        public RegistryHookValue? Read(string key, string name) => Values.GetValueOrDefault((key.ToLowerInvariant(), name.ToLowerInvariant()));

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

    private readonly FakeRegistry _registry = new();
    private string HookBackup => RegistryHookSession.BackupPathFor(_root, Id);

    /// <summary>模拟 SessionHost 崩溃后遗留的钩子备份：插件把 Software\Vendor\Game 下的 v 从 1 改成了 99，原值 1 在备份里。</summary>
    private void LeaveCrashedHookBackup()
    {
        _registry.Values[(@"software\vendor\game", "v")] = new RegistryHookValue(RegistryHookValue.Dword, "99");
        Directory.CreateDirectory(Path.GetDirectoryName(HookBackup)!);
        File.WriteAllText(HookBackup, """
            { "pluginId": "ext.acme.demo", "prefixes": ["Software\\Vendor\\Game"],
              "entries": [ { "key": "Software\\Vendor\\Game", "name": "v", "existed": true, "kind": "dword", "data": "1" } ] }
            """);
    }

    [Fact]
    public void 列出插件且默认未启用()
    {
        var (vm, _, _) = Create();

        var item = Assert.Single(vm.Items);
        Assert.Contains("演示", item.Title);
        Assert.Contains("未启用", item.Status);
        Assert.True(item.CanEnable);
        Assert.False(item.CanDisable);
        Assert.Contains("会注入游戏进程", item.Detail);
    }

    [Fact]
    public async Task 启用_停用_删除后触发Changed_构造时不触发()
    {
        var fired = 0;
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var vm = new PluginsViewModel(new PluginCatalog(Path.Combine(_root, "plugins"), trust), trust, _root, null, _registry);
        vm.Changed += () => fired++;
        Assert.Equal(0, fired); // 构造时的首次读取不算变化
        vm.Confirm = _ => Task.FromResult(true);

        await vm.EnableAsync(Id);
        Assert.True(fired >= 1);

        var afterEnable = fired;
        vm.Disable(Id);
        Assert.True(fired > afterEnable);

        var afterDisable = fired;
        await vm.EnableAsync(Id);
        await vm.DeleteAsync(Id);
        Assert.True(fired > afterDisable);
    }

    [Fact]
    public async Task 用户拒绝确认时不写信任记录()
    {
        var (vm, trust, _) = Create();
        vm.Confirm = _ => Task.FromResult(false);

        await vm.EnableAsync(Id);

        Assert.Null(trust.Find(Id));
    }

    [Fact]
    public async Task 默认没有界面接管时一律视为拒绝()
    {
        var (vm, trust, _) = Create();

        await vm.EnableAsync(Id);

        Assert.Null(trust.Find(Id));
    }

    [Fact]
    public async Task 用户确认后写入信任记录并标为已启用()
    {
        var (vm, trust, _) = Create();
        PluginConfirmation? shown = null;
        vm.Confirm = request =>
        {
            shown = request;
            return Task.FromResult(true);
        };

        await vm.EnableAsync(Id);

        Assert.NotNull(trust.Find(Id));
        Assert.Equal("已启用", Assert.Single(vm.Items).Status);
        Assert.NotNull(shown);
        Assert.Contains(trust.Find(Id)!.DllSha256, shown!.Body);
        Assert.Contains("管理员权限", shown.Body);
        Assert.Contains("注入", shown.Body);
        Assert.Contains("Demo.exe", shown.Body);
    }

    [Fact]
    public async Task 确认期间DLL被替换则取消启用()
    {
        var (vm, trust, _) = Create();
        vm.Confirm = _ =>
        {
            File.WriteAllText(Path.Combine(PluginDir, "module.dll"), "dll-v2-tampered");
            return Task.FromResult(true);
        };

        await vm.EnableAsync(Id);

        Assert.Null(trust.Find(Id));
        Assert.Contains("发生了变化", vm.Message);
    }

    [Fact]
    public async Task DLL变化后需要重新确认()
    {
        var (vm, _, _) = Create();
        vm.Confirm = _ => Task.FromResult(true);
        await vm.EnableAsync(Id);

        File.WriteAllText(Path.Combine(PluginDir, "module.dll"), "dll-v2");
        vm.Refresh();

        var item = Assert.Single(vm.Items);
        Assert.Contains("重新确认", item.Status);
        Assert.Equal("重新确认并启用", item.EnableLabel);
    }

    [Fact]
    public async Task 停用后撤销信任但保留文件()
    {
        var (vm, trust, _) = Create();
        vm.Confirm = _ => Task.FromResult(true);
        await vm.EnableAsync(Id);

        vm.Disable(Id);

        Assert.Null(trust.Find(Id));
        Assert.True(File.Exists(Path.Combine(PluginDir, "module.dll")));
        Assert.True(Assert.Single(vm.Items).CanEnable);
    }

    [Fact]
    public async Task 遗留备份无法恢复时拒绝删除插件()
    {
        var (vm, trust, removed) = Create();
        vm.Confirm = _ => Task.FromResult(true);
        await vm.EnableAsync(Id);
        LeaveCrashedHookBackup();
        _registry.FailWrites = true; // 恢复原值要写注册表，让它失败

        await vm.DeleteAsync(Id);

        Assert.True(Directory.Exists(PluginDir)); // 没删
        Assert.True(File.Exists(HookBackup)); // 备份保留
        Assert.NotNull(trust.Find(Id));
        Assert.Empty(removed);
        Assert.Contains("未删除", vm.Message);
        Assert.Equal("99", _registry.Read(@"Software\Vendor\Game", "v")!.Data);
    }

    [Fact]
    public async Task 停用时先恢复遗留备份()
    {
        var (vm, trust, _) = Create();
        vm.Confirm = _ => Task.FromResult(true);
        await vm.EnableAsync(Id);
        LeaveCrashedHookBackup();

        vm.Disable(Id);

        Assert.Null(trust.Find(Id));
        Assert.Equal("1", _registry.Read(@"Software\Vendor\Game", "v")!.Data);
        Assert.False(File.Exists(HookBackup));
    }

    [Fact]
    public async Task 停用时遗留备份恢复失败也会停用_但提示备份仍保留()
    {
        var (vm, trust, _) = Create();
        vm.Confirm = _ => Task.FromResult(true);
        await vm.EnableAsync(Id);
        LeaveCrashedHookBackup();
        _registry.FailWrites = true;

        vm.Disable(Id);

        Assert.Null(trust.Find(Id));
        Assert.True(File.Exists(HookBackup));
        Assert.Contains("备份", vm.Message);
    }

    [Fact]
    public async Task 删除会移除目录_信任记录_恢复备份并通知上层()
    {
        var (vm, trust, removed) = Create();
        vm.Confirm = _ => Task.FromResult(true);
        await vm.EnableAsync(Id);
        LeaveCrashedHookBackup(); // SessionHost 崩溃后遗留的钩子备份（真实文件名 registry-hook-<id>.json）
        var backup = HookBackup;

        await vm.DeleteAsync(Id);

        Assert.False(Directory.Exists(PluginDir));
        Assert.False(File.Exists(backup)); // 恢复成功后备份被消耗
        Assert.Equal("1", _registry.Read(@"Software\Vendor\Game", "v")!.Data); // HKCU 被恢复成原值，而不是停在插件改写后的 99
        Assert.Null(trust.Find(Id));
        Assert.Equal([Id], removed);
        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task 取消删除时什么都不动()
    {
        var (vm, _, removed) = Create();
        vm.Confirm = _ => Task.FromResult(false);

        await vm.DeleteAsync(Id);

        Assert.True(Directory.Exists(PluginDir));
        Assert.Empty(removed);
    }
}
