using System.Text;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>坏的 / 不兼容的插件只能让它自己显示为"不可用"，绝不能让目录扫描（主程序启动、Broker）抛异常。</summary>
public sealed class PluginRobustnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-robust-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> BadManifests => new()
    {
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": null, "caps": [] }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["a.exe"], "caps": null }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": [null], "caps": [] }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["a.exe"], "caps": [], "registryHooks": { "keys": null } }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["a.exe"], "caps": [], "autoFind": { "uninstallNames": null } }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["a.exe"], "caps": [], "autoFind": { "registry": [null] } }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["a.exe"], "caps": [], "backgroundApi": null, "icon": 5 }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": "one", "exeNames": ["a.exe"], "caps": [] }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["a.exe"], "caps": [], "settingsSchema": 7 }""",
        """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 99, "exeNames": ["a.exe"], "caps": ["fly"], "settingsSchema": { "fields": [ { "type": "hologram" } ] } }""",
        "[]",
        "null",
        "\"text\"",
        "{",
    };

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void 解析坏的manifest不抛异常(string json)
    {
        var ok = PluginManifest.TryParse(Encoding.UTF8.GetBytes(json), "ext.acme.demo", out _, out var error);

        Assert.False(ok && error.Length > 0);
        _ = error;
    }

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void 目录扫描遇到坏插件不抛异常_该插件只是不可用(string json)
    {
        var dir = Path.Combine(_root, "plugins", "ext.acme.demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), json);
        File.WriteAllBytes(Path.Combine(dir, "module.dll"), [1, 2, 3]);
        var good = Path.Combine(_root, "plugins", "ext.acme.good");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "plugin.json"), """{ "id": "ext.acme.good", "displayName": "G", "abiMajor": 1, "exeNames": ["g.exe"], "caps": [] }""");
        File.WriteAllBytes(Path.Combine(good, "module.dll"), [1, 2, 3]);
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var catalog = new PluginCatalog(Path.Combine(_root, "plugins"), trust);

        var entries = catalog.Scan();

        Assert.Equal(2, entries.Count);
        Assert.NotEqual(PluginAvailability.Ready, entries.Single(e => e.Id == "ext.acme.demo").Availability);
        Assert.Equal(PluginAvailability.Untrusted, entries.Single(e => e.Id == "ext.acme.good").Availability); // 好插件不受影响
        _ = catalog.All;
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ \"records\": 5 }")]
    [InlineData("")]
    public void 信任文件损坏时扫描不抛异常(string content)
    {
        var dir = Path.Combine(_root, "plugins", "ext.acme.good");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{ "id": "ext.acme.good", "displayName": "G", "abiMajor": 1, "exeNames": ["g.exe"], "caps": [] }""");
        File.WriteAllBytes(Path.Combine(dir, "module.dll"), [1]);
        var trustPath = Path.Combine(_root, "plugin-trust.json");
        File.WriteAllText(trustPath, content);

        var entries = new PluginCatalog(Path.Combine(_root, "plugins"), new PluginTrustStore(trustPath)).Scan();

        Assert.Single(entries);
    }
}
