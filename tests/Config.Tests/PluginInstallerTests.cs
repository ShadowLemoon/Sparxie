using System.IO.Compression;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

public sealed class PluginInstallerTests : IDisposable
{
    private const string Id = "ext.acme.demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-installer-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Plugins => Path.Combine(_root, "plugins");

    private string Staging => Path.Combine(_root, "plugin-staging");

    private static string Manifest(string id = Id) =>
        $$"""{ "id": "{{id}}", "displayName": "Demo", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [] }""";

    private string MakeFolder(string name, string? id = Id, bool withDll = true)
    {
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(dir);
        if (id is not null)
        {
            File.WriteAllText(Path.Combine(dir, "plugin.json"), Manifest(id));
        }

        if (withDll)
        {
            File.WriteAllBytes(Path.Combine(dir, "module.dll"), [1, 2, 3, 4]);
        }

        return dir;
    }

    private string MakeZip(string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(_root, name + ".zip");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            fill(archive);
        }

        return path;
    }

    private static void Add(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    [Fact]
    public void 文件夹导入成功_放到plugins的id目录下_不留暂存()
    {
        var result = PluginInstaller.Install(MakeFolder("a"), Plugins, Staging);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Id, result.Id);
        Assert.True(File.Exists(Path.Combine(Plugins, Id, "module.dll")));
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 压缩包导入成功_根目录或唯一子目录都行(bool nested)
    {
        var prefix = nested ? "ext.acme.demo/" : string.Empty;
        var zip = MakeZip("p", archive =>
        {
            Add(archive, prefix + "plugin.json", Manifest());
            Add(archive, prefix + "module.dll", "dll");
        });

        var result = PluginInstaller.Install(zip, Plugins, Staging);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(Plugins, Id, "plugin.json")));
    }

    [Fact]
    public void 压缩包含越界路径被拒绝_且没有写到目录外()
    {
        var zip = MakeZip("evil", archive =>
        {
            Add(archive, "plugin.json", Manifest());
            Add(archive, "module.dll", "dll");
            Add(archive, "../escaped.txt", "x");
        });

        var result = PluginInstaller.Install(zip, Plugins, Staging);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(Plugins, Id)));
    }

    [Theory]
    [InlineData("nojson")]
    [InlineData("nodll")]
    [InlineData("badid")]
    [InlineData("builtin")]
    public void 不合格的插件被拒绝_不改动plugins目录(string kind)
    {
        var dir = kind switch
        {
            "nojson" => MakeFolder(kind, id: null),
            "nodll" => MakeFolder(kind, withDll: false),
            "badid" => MakeFolder(kind, id: "Bad Id"),
            _ => MakeFolder(kind, id: "genshin"),
        };

        var result = PluginInstaller.Install(dir, Plugins, Staging);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Plugins) && Directory.GetDirectories(Plugins).Length > 0);
    }

    [Fact]
    public void 同id已存在时整体替换_旧文件不残留()
    {
        var first = MakeFolder("v1");
        File.WriteAllText(Path.Combine(first, "old.txt"), "old");
        Assert.True(PluginInstaller.Install(first, Plugins, Staging).Success);

        var second = MakeFolder("v2");
        File.WriteAllBytes(Path.Combine(second, "module.dll"), [9, 9, 9]);
        var result = PluginInstaller.Install(second, Plugins, Staging);

        Assert.True(result.Success);
        Assert.True(result.Replaced);
        Assert.False(File.Exists(Path.Combine(Plugins, Id, "old.txt")));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(Plugins, Id, "module.dll")));
    }

    [Fact]
    public void 不是zip也不是文件夹时给出提示()
    {
        var file = Path.Combine(_root, "x.txt");
        File.WriteAllText(file, "x");

        Assert.False(PluginInstaller.Install(file, Plugins, Staging).Success);
        Assert.False(PluginInstaller.Install(Path.Combine(_root, "missing"), Plugins, Staging).Success);
    }

    [Fact]
    public void 导入后不会自动启用_目录扫描结果是未确认()
    {
        PluginInstaller.Install(MakeFolder("a"), Plugins, Staging);
        var catalog = new PluginCatalog(Plugins, new PluginTrustStore(Path.Combine(_root, "trust.json")));

        Assert.Equal(PluginAvailability.Untrusted, catalog.Scan().Single().Availability);
    }
}
