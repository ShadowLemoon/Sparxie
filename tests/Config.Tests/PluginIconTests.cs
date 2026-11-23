using System.Text;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

public sealed class PluginIconTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-icon-" + Guid.NewGuid().ToString("N"));

    public PluginIconTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Put(string name) => File.WriteAllBytes(Path.Combine(_dir, name), [1, 2, 3]);

    [Fact]
    public void manifest可以声明ico作为icon()
    {
        var json = """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["D.exe"], "icon": "app.ico" }""";

        Assert.True(PluginManifest.TryParse(Encoding.UTF8.GetBytes(json), "ext.acme.demo", out var manifest, out var error), error);
        Assert.Equal("app.ico", manifest.Icon);
    }

    [Fact]
    public void 没声明icon时按约定找icon_ico再icon_png()
    {
        Put("icon.png");
        Assert.Equal(Path.Combine(_dir, "icon.png"), PluginAssets.ResolveIcon(_dir, null));

        Put("icon.ico");
        Assert.Equal(Path.Combine(_dir, "icon.ico"), PluginAssets.ResolveIcon(_dir, null));
    }

    [Fact]
    public void 声明的icon优先_声明的文件不可用时退回约定名()
    {
        Put("custom.ico");
        Put("icon.png");

        Assert.Equal(Path.Combine(_dir, "custom.ico"), PluginAssets.ResolveIcon(_dir, "custom.ico"));
        Assert.Equal(Path.Combine(_dir, "icon.png"), PluginAssets.ResolveIcon(_dir, "missing.ico"));
        Assert.Null(PluginAssets.ResolveIcon(Path.Combine(_dir, "empty"), null));
    }

    [Fact]
    public void ico之外的可执行文件扩展名仍然拒绝()
    {
        Assert.True(PluginAssets.IsValidFileName("a.ico"));
        Assert.False(PluginAssets.IsValidFileName("a.exe"));
        Assert.False(PluginAssets.IsValidFileName("..\\a.ico"));
    }
}
