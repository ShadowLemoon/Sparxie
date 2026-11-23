using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

public sealed class PluginDropTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-drop-" + Guid.NewGuid().ToString("N"));

    public PluginDropTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 拖放只接受zip和文件夹_其余列为不支持()
    {
        var folder = Path.Combine(_root, "plugin-folder");
        Directory.CreateDirectory(folder);

        var result = PluginDropFilter.Pick([folder, @"C:\x\a.ZIP", @"C:\x\readme.txt", @"C:\x\game.exe", null, " "]);

        Assert.Equal([folder, @"C:\x\a.ZIP"], result.Accepted.ToArray());
        Assert.Equal(["readme.txt", "game.exe"], result.Rejected.ToArray());
        Assert.False(result.TooMany);
    }

    [Fact]
    public void 一次最多导入8个()
    {
        var paths = Enumerable.Range(0, 12).Select(i => $@"C:\x\p{i}.zip").ToList();

        var result = PluginDropFilter.Pick(paths);

        Assert.Equal(PluginDropFilter.MaxItems, result.Accepted.Count);
        Assert.True(result.TooMany);
    }

    private PluginsViewModel Create()
    {
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        return new PluginsViewModel(new PluginCatalog(Path.Combine(_root, "plugins"), trust), trust, _root);
    }

    [Fact]
    public void 没有可导入内容时给出明确提示()
    {
        var vm = Create();

        vm.ReportDropProblem(PluginDropFilter.Pick([@"C:\x\readme.txt"]));

        Assert.Contains("请拖入插件的 .zip 压缩包或文件夹", vm.Message);
        Assert.Contains("readme.txt", vm.Message);
    }

    [Fact]
    public void 拖入的文件夹和zip依次导入_都不自动启用()
    {
        var vm = Create();
        string Make(string id)
        {
            var dir = Path.Combine(_root, "src", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"),
                $$"""{ "id": "{{id}}", "displayName": "{{id}}", "abiMajor": 1, "exeNames": ["{{id.Split('.')[2]}}.exe"], "caps": [] }""");
            File.WriteAllText(Path.Combine(dir, "module.dll"), "dll");
            return dir;
        }

        var zipSource = Make("ext.acme.zipped");
        var zip = Path.Combine(_root, "zipped.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(zipSource, zip);
        var picked = PluginDropFilter.Pick([Make("ext.acme.folder"), zip]);

        foreach (var path in picked.Accepted)
        {
            vm.ImportAsync(path).GetAwaiter().GetResult();
        }

        Assert.Equal(["ext.acme.folder", "ext.acme.zipped"], vm.Items.Select(item => item.Entry.Id).Order().ToArray());
        Assert.All(vm.Items, item => Assert.True(item.CanEnable)); // 仍是“未启用”，需要用户确认
    }
}
