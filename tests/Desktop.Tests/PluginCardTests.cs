using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

/// <summary>插件卡片的状态表、排序、条目内消息与运行中保护（设计文档 P0-2 / P0-7）。</summary>
public sealed class PluginCardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-plugin-card-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Install(string id, string name, string exe = "Demo.exe", string? extra = null)
    {
        var dir = Path.Combine(_root, "plugins", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{id}}", "displayName": "{{name}}", "abiMajor": 1, "exeNames": ["{{exe}}"], "caps": [], "version": "1.0" {{extra}} }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), "dll-" + id);
    }

    private (PluginsViewModel Vm, PluginTrustStore Trust) Create()
    {
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var catalog = new PluginCatalog(Path.Combine(_root, "plugins"), trust);
        return (new PluginsViewModel(catalog, trust, _root), trust);
    }

    [Fact]
    public void 状态徽标_图标_颜色级别_排序()
    {
        Install("ext.acme.aaa", "已启用的");
        Install("ext.acme.bbb", "未启用的");
        Install("ext.acme.ccc", "变化的");
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "ext.acme.zzz")); // 缺 plugin.json：无效
        var (vm, trust) = Create();
        var scan = new PluginCatalog(Path.Combine(_root, "plugins"), trust).Scan();
        foreach (var id in new[] { "ext.acme.aaa", "ext.acme.ccc" })
        {
            var entry = scan.Single(item => item.Id == id);
            trust.Confirm(new PluginTrustRecord(id, id == "ext.acme.ccc" ? "stale" : entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow));
        }

        vm.Refresh();

        Assert.Equal(["ext.acme.ccc", "ext.acme.bbb", "ext.acme.zzz", "ext.acme.aaa"], vm.Items.Select(item => item.Entry.Id).ToArray());
        Assert.Equal(["caution", "neutral", "critical", "success"], vm.Items.Select(item => item.StatusLevel).ToArray());
        Assert.All(vm.Items, item => Assert.NotEmpty(item.StatusGlyph)); // 颜色不是唯一信号
        Assert.True(vm.Items[3].IsStatusSuccess);
        Assert.Equal("已启用的", vm.Items[3].DisplayName);
        Assert.Equal("ext.acme.aaa · v1.0", vm.Items[3].Subtitle);
    }

    [Fact]
    public void 导入成功不弹提示_状态徽标和按钮已经说明结果()
    {
        var (vm, _) = Create();
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plugin.json"), """{ "id": "ext.acme.new", "displayName": "新插件", "abiMajor": 1, "exeNames": ["N.exe"], "caps": [] }""");
        File.WriteAllText(Path.Combine(source, "module.dll"), "dll");

        vm.ImportAsync(source).GetAwaiter().GetResult();

        var card = Assert.Single(vm.Items);
        Assert.False(card.HasNotice); // 成功的操作不弹提示（文字原则）
        Assert.Equal("未启用", card.Status);
        Assert.True(card.CanEnable);
        Assert.False(vm.ShowStandaloneMessage);
        Assert.Empty(vm.Message);
    }

    [Fact]
    public void 导入失败没有对应卡片_消息单独显示()
    {
        var (vm, _) = Create();

        vm.ImportAsync(Path.Combine(_root, "不存在")).GetAwaiter().GetResult();

        Assert.Empty(vm.Items);
        Assert.True(vm.ShowStandaloneMessage);
        Assert.NotEmpty(vm.Message);
    }

    [Fact]
    public void 游戏运行中_停用和删除被禁用并说明原因()
    {
        // 用当前测试进程自己的 EXE 名冒充“游戏正在运行”。
        var exe = Path.GetFileName(Environment.ProcessPath)!;
        Install("ext.acme.run", "运行中", exe);
        var (vm, trust) = Create();
        var entry = new PluginCatalog(Path.Combine(_root, "plugins"), trust).Scan().Single();
        trust.Confirm(new PluginTrustRecord(entry.Id, entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow));
        vm.Refresh();

        var card = vm.Items.Single();

        Assert.True(card.CanDisableVisible);
        Assert.False(card.CanDisable);
        Assert.False(card.CanDelete);
        Assert.Contains("先退出游戏", card.BlockReason);
    }

    [Fact]
    public void 有遗留的注册表备份时卡片标记待恢复()
    {
        Install("ext.acme.reg", "有备份", "SurelyNotRunning-5.exe");
        var backup = RegistryHookSession.BackupPathFor(_root, "ext.acme.reg");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "{}");
        var (vm, _) = Create();

        Assert.True(vm.Items.Single().HasPendingBackup);
        File.Delete(backup);
        vm.Refresh();
        Assert.False(vm.Items.Single().HasPendingBackup);
    }

    [Fact]
    public void 游戏没运行时可以停用和删除()
    {
        Install("ext.acme.idle", "空闲", "SurelyNotRunning-123.exe");
        var (vm, _) = Create();

        var card = vm.Items.Single();

        Assert.True(card.CanDelete);
        Assert.Null(card.BlockReason);
    }
}
