using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

public sealed class PluginInstallerSameContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-installer4-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerSameContentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Make(string name, bool withIcon)
    {
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            """{ "id": "ext.acme.demo", "displayName": "演示", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [], "version": "1.0" }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), "dll");
        if (withIcon) { File.WriteAllBytes(Path.Combine(dir, "icon.ico"), [1, 2, 3]); }
        return dir;
    }

    [Fact]
    public void 只是多了图标也不算相同_否则图标更新会被跳过()
    {
        var plugins = Path.Combine(_root, "plugins");
        var staging = Path.Combine(_root, "staging");
        Assert.True(PluginInstaller.Install(Make("a", withIcon: false), plugins, staging).Success);

        var inspection = PluginInstaller.Inspect(Make("b", withIcon: true), plugins, staging);

        Assert.NotEqual(PluginInstallKind.Identical, inspection.Kind);
        Assert.True(PluginInstaller.Commit(inspection, plugins, staging).Success);
        Assert.True(File.Exists(Path.Combine(plugins, "ext.acme.demo", "icon.ico")));
    }
}
