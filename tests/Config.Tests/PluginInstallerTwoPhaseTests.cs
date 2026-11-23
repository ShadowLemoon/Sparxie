using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

public sealed class PluginInstallerTwoPhaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-installer2-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerTwoPhaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Plugins => Path.Combine(_root, "plugins");

    private string Staging => Path.Combine(_root, "staging");

    private string Make(string name, string version, string dll = "dll")
    {
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "ext.acme.demo", "displayName": "演示", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [], "version": "{{version}}" }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), dll);
        return dir;
    }

    [Fact]
    public void 检查阶段不动plugins目录_放弃后暂存清空()
    {
        var inspection = PluginInstaller.Inspect(Make("a", "1.0"), Plugins, Staging);

        Assert.True(inspection.Success);
        Assert.Equal(PluginInstallKind.New, inspection.Kind);
        Assert.False(Directory.Exists(Path.Combine(Plugins, "ext.acme.demo")));
        Assert.Single(Directory.GetDirectories(Staging));

        PluginInstaller.Discard(inspection);

        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Fact]
    public void 提交后才出现在plugins目录()
    {
        var inspection = PluginInstaller.Inspect(Make("a", "1.0"), Plugins, Staging);

        var result = PluginInstaller.Commit(inspection, Plugins, Staging);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(Plugins, "ext.acme.demo", "module.dll")));
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Theory]
    [InlineData("1.0", "2.0", PluginInstallKind.Upgrade)]
    [InlineData("2.0", "1.0", PluginInstallKind.Downgrade)]
    [InlineData("1.0", "1.0", PluginInstallKind.Reinstall)]
    public void 与已安装版本比较(string installed, string incoming, PluginInstallKind expected)
    {
        Assert.True(PluginInstaller.Install(Make("old", installed, "old-dll"), Plugins, Staging).Success);

        var inspection = PluginInstaller.Inspect(Make("new", incoming, "new-dll"), Plugins, Staging);

        Assert.Equal(expected, inspection.Kind);
        Assert.Equal(installed, inspection.ExistingVersion);
        PluginInstaller.Discard(inspection);
    }

    [Fact]
    public void 内容完全相同时识别为Identical()
    {
        Assert.True(PluginInstaller.Install(Make("a", "1.0"), Plugins, Staging).Success);

        var inspection = PluginInstaller.Inspect(Make("b", "1.0"), Plugins, Staging);

        Assert.Equal(PluginInstallKind.Identical, inspection.Kind);
        PluginInstaller.Discard(inspection);
    }

    [Fact]
    public void 失败类型可区分()
    {
        Assert.Equal(PluginInstallFailure.NotSupported, PluginInstaller.Inspect(Path.Combine(_root, "nothing"), Plugins, Staging).Failure);

        var zip = Path.Combine(_root, "evil.zip");
        using (var stream = File.Create(zip))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "plugin.json", "module.dll", "../x.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name == "plugin.json" ? """{ "id": "ext.acme.demo" }""" : "x");
            }
        }

        Assert.Equal(PluginInstallFailure.UnsafePath, PluginInstaller.Inspect(zip, Plugins, Staging).Failure);

        var noDll = Make("nodll", "1.0");
        File.Delete(Path.Combine(noDll, "module.dll"));
        Assert.Equal(PluginInstallFailure.NoModule, PluginInstaller.Inspect(noDll, Plugins, Staging).Failure);

        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "x");
        Assert.Equal(PluginInstallFailure.NoManifest, PluginInstaller.Inspect(empty, Plugins, Staging).Failure);
    }
}
