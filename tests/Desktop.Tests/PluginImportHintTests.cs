using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

/// <summary>导入替换后的提示要和插件的实际状态一致：哈希没变仍是已启用，变了才要求重新确认。</summary>
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
    public void 只换了图标_哈希没变_提示仍保持启用_而不是要求重新确认()
    {
        var vm = CreateEnabled();

        vm.ImportAsync(Make("b", "dll-v1", icon: true)).GetAwaiter().GetResult();

        var card = vm.Items.Single();
        Assert.Contains("仍保持启用", card.Notice);
        Assert.DoesNotContain("重新确认", card.Notice);
        Assert.True(card.CanDisableVisible);
        Assert.False(card.CanEnable);
    }

    [Fact]
    public void 模块变了_提示重新确认_卡片出现重新确认并启用()
    {
        var vm = CreateEnabled();

        vm.ImportAsync(Make("c", "dll-v2", icon: false)).GetAwaiter().GetResult();

        var card = vm.Items.Single();
        Assert.Contains("重新确认并启用", card.Notice);
        Assert.True(card.CanEnable);
        Assert.Equal("重新确认并启用", card.EnableLabel);
        Assert.False(card.CanDisableVisible);
    }
}
