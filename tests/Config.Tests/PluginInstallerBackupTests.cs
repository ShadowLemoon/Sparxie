using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

public sealed class PluginInstallerBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-installer3-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerBackupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Make(string name, string dll)
    {
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            """{ "id": "ext.acme.demo", "displayName": "演示", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [], "version": "1.0" }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), dll);
        File.WriteAllText(Path.Combine(dir, "sub", "extra.txt"), dll);
        return dir;
    }

    [Fact]
    public void 替换后备份已清理_暂存放在别处也能导入含子目录的插件()
    {
        var plugins = Path.Combine(_root, "plugins");
        var staging = Path.Combine(_root, "elsewhere", "staging");
        Assert.True(PluginInstaller.Install(Make("a", "v1"), plugins, staging).Success);

        var result = PluginInstaller.Install(Make("b", "v2"), plugins, staging);

        Assert.True(result.Success);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(plugins, "ext.acme.demo", "sub", "extra.txt")));
        Assert.Equal(["ext.acme.demo"], Directory.GetDirectories(plugins).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void 以点开头的目录被插件扫描忽略()
    {
        var plugins = Path.Combine(_root, "plugins");
        Assert.True(PluginInstaller.Install(Make("a", "v1"), plugins, Path.Combine(_root, "staging")).Success);
        var leftover = Path.Combine(plugins, ".old-leftover");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "plugin.json"), "{}");

        var entries = new PluginCatalog(plugins, new PluginTrustStore(Path.Combine(_root, "trust.json"))).Scan();

        Assert.Equal(["ext.acme.demo"], entries.Select(entry => entry.Id).ToArray());
    }
}
