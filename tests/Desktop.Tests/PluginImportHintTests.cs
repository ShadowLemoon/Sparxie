using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

/// <summary>导入替换后：状态由徽标和按钮表达（哈希没变仍是已启用，变了才出现“重新确认并启用”），成功不弹提示。</summary>
public sealed class PluginImportHintTests : IDisposable
{
    private const string Id = "ext.acme.hint";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-import-hint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Make(string name, string dll, bool icon)
    {
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{Id}}", "displayName": "提示", "abiMajor": 1, "exeNames": ["SurelyNotRunning-7.exe"], "caps": [], "version": "1.0" }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), dll);
        if (icon) { File.WriteAllBytes(Path.Combine(dir, "icon.ico"), [1, 2, 3]); }
        return dir;
    }

    private PluginsViewModel CreateEnabled()
    {
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var vm = new PluginsViewModel(new PluginCatalog(Path.Combine(_root, "plugins"), trust), trust, _root)
        {
            Confirm = _ => Task.FromResult(true),
        };
        vm.ImportAsync(Make("a", "dll-v1", icon: false)).GetAwaiter().GetResult();
        vm.EnableAsync(Id).GetAwaiter().GetResult();
        Assert.True(vm.Items.Single().CanDisableVisible); // 已启用
        return vm;
    }

    [Fact]
    public void 只换了图标_哈希没变_仍是已启用_没有提示()
    {
        var vm = CreateEnabled();

        vm.ImportAsync(Make("b", "dll-v1", icon: true)).GetAwaiter().GetResult();

        var card = vm.Items.Single();
        Assert.Equal("已启用", card.Status);
        Assert.False(card.HasNotice);
        Assert.True(card.CanDisableVisible);
        Assert.False(card.CanEnable);
    }

    [Fact]
    public void 模块变了_徽标是文件已变化_卡片出现重新确认并启用_没有提示()
    {
        var vm = CreateEnabled();

        vm.ImportAsync(Make("c", "dll-v2", icon: false)).GetAwaiter().GetResult();

        var card = vm.Items.Single();
        Assert.Equal("文件已变化", card.Status);
        Assert.False(card.HasNotice);
        Assert.True(card.CanEnable);
        Assert.Equal("重新确认并启用", card.EnableLabel);
        Assert.False(card.CanDisableVisible);
    }

    [Fact]
    public void 启用和停用成功都不弹提示()
    {
        var vm = CreateEnabled();
        Assert.Empty(vm.Message);

        vm.Disable(Id);

        Assert.False(vm.Items.Single().HasNotice);
        Assert.Empty(vm.Message);
        Assert.Equal("未启用", vm.Items.Single().Status);
    }
}
