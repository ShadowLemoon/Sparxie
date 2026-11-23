using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

public sealed class PluginImportFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-import-flow-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Make(string name, string version, string dll)
    {
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "ext.acme.flow", "displayName": "流程", "abiMajor": 1, "exeNames": ["SurelyNotRunning-9.exe"], "caps": [], "version": "{{version}}" }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), dll);
        return dir;
    }

    private (PluginsViewModel Vm, List<PluginConfirmation> Asked) Create(bool accept)
    {
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var vm = new PluginsViewModel(new PluginCatalog(Path.Combine(_root, "plugins"), trust), trust, _root);
        var asked = new List<PluginConfirmation>();
        vm.Confirm = confirmation =>
        {
            asked.Add(confirmation);
            return Task.FromResult(accept);
        };
        return (vm, asked);
    }

    [Fact]
    public void 新装不额外确认()
    {
        var (vm, asked) = Create(accept: false);

        vm.ImportAsync(Make("a", "1.0", "d1")).GetAwaiter().GetResult();

        Assert.Empty(asked);
        Assert.True(Directory.Exists(Path.Combine(_root, "plugin-staging"))); // 数据目录可写时暂存在同盘，提交是改名而不是二次拷贝
        Assert.Single(vm.Items);
    }

    [Fact]
    public void 升级要确认_拒绝后不替换()
    {
        var (vm, asked) = Create(accept: false);
        vm.ImportAsync(Make("a", "1.0", "d1")).GetAwaiter().GetResult();

        vm.ImportAsync(Make("b", "2.0", "d2")).GetAwaiter().GetResult();

        Assert.Contains("升级", Assert.Single(asked).Body);
        Assert.Equal("d1", File.ReadAllText(Path.Combine(_root, "plugins", "ext.acme.flow", "module.dll")));
    }

    [Fact]
    public void 降级的提示更明确_接受后替换()
    {
        var (vm, asked) = Create(accept: true);
        vm.ImportAsync(Make("a", "2.0", "d2")).GetAwaiter().GetResult();

        vm.ImportAsync(Make("b", "1.0", "d1")).GetAwaiter().GetResult();

        Assert.Contains("降级", Assert.Single(asked).Body);
        Assert.Equal("d1", File.ReadAllText(Path.Combine(_root, "plugins", "ext.acme.flow", "module.dll")));
    }

    [Fact]
    public void 内容相同直接跳过_不弹确认()
    {
        var (vm, asked) = Create(accept: true);
        vm.ImportAsync(Make("a", "1.0", "d1")).GetAwaiter().GetResult();

        vm.ImportAsync(Make("b", "1.0", "d1")).GetAwaiter().GetResult();

        Assert.Empty(asked);
        Assert.Contains("没有改动", vm.Items.Single().Notice);
    }
}
