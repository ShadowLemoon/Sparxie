using System.Security.Cryptography;
using System.Text;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

public sealed class PluginCatalogTests : IDisposable
{
    private const string Id = "ext.acme.demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-plugin-tests", Guid.NewGuid().ToString("N"));
    private string PluginsDir => Path.Combine(_root, "plugins");
    private string TrustPath => Path.Combine(_root, "plugin-trust.json");

    public PluginCatalogTests() => Directory.CreateDirectory(PluginsDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private static string ManifestJson(string id = Id, string exe = "Demo.exe", int abi = 1) => $$"""
        { "id": "{{id}}", "displayName": "演示", "abiMajor": {{abi}}, "exeNames": ["{{exe}}"], "caps": ["injects"] }
        """;

    private string WritePlugin(string id = Id, string? manifest = null, string dll = "dll-bytes")
    {
        var dir = Path.Combine(PluginsDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest ?? ManifestJson(id));
        File.WriteAllText(Path.Combine(dir, "module.dll"), dll);
        return dir;
    }

    private PluginCatalog Catalog() => new(PluginsDir, new PluginTrustStore(TrustPath));

    private static PluginTrustRecord TrustFor(PluginEntry entry) =>
        new(entry.Id, entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow);

    [Fact]
    public void 目录不存在时为空()
    {
        Assert.Empty(new PluginCatalog(Path.Combine(_root, "none"), new PluginTrustStore(TrustPath)).Scan());
    }

    [Fact]
    public void 默认未信任且不出现在游戏目录里()
    {
        WritePlugin();
        var catalog = Catalog();

        var entry = Assert.Single(catalog.Scan());
        Assert.Equal(PluginAvailability.Untrusted, entry.Availability);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dll-bytes"))).ToLowerInvariant(), entry.DllSha256);
        Assert.Empty(catalog.All);
        Assert.Null(catalog.Find(Id));
    }

    [Fact]
    public void 用户确认后Ready并暴露EXE白名单()
    {
        WritePlugin();
        var trust = new PluginTrustStore(TrustPath);
        var catalog = new PluginCatalog(PluginsDir, trust);
        trust.Confirm(TrustFor(catalog.Scan()[0]));

        var descriptor = Assert.Single(catalog.All);
        Assert.Equal(Id, descriptor.Id);
        Assert.Equal(["Demo.exe"], descriptor.ExeNames);
        Assert.Equal(PluginAvailability.Ready, catalog.Availability(Id));
    }

    [Fact]
    public void DLL被替换后需要重新确认()
    {
        var dir = WritePlugin();
        var trust = new PluginTrustStore(TrustPath);
        var catalog = new PluginCatalog(PluginsDir, trust);
        trust.Confirm(TrustFor(catalog.Scan()[0]));

        File.WriteAllText(Path.Combine(dir, "module.dll"), "tampered");

        Assert.Equal(PluginAvailability.HashChanged, catalog.Availability(Id));
        Assert.Empty(catalog.All);
    }

    [Fact]
    public void manifest被改写后需要重新确认()
    {
        var dir = WritePlugin();
        var trust = new PluginTrustStore(TrustPath);
        var catalog = new PluginCatalog(PluginsDir, trust);
        trust.Confirm(TrustFor(catalog.Scan()[0]));

        File.WriteAllText(Path.Combine(dir, "plugin.json"), ManifestJson(exe: "Other.exe"));

        Assert.Equal(PluginAvailability.HashChanged, catalog.Availability(Id));
    }

    [Fact]
    public void 移除信任记录后回到未信任()
    {
        WritePlugin();
        var trust = new PluginTrustStore(TrustPath);
        var catalog = new PluginCatalog(PluginsDir, trust);
        trust.Confirm(TrustFor(catalog.Scan()[0]));
        trust.Remove(Id);

        Assert.Equal(PluginAvailability.Untrusted, catalog.Availability(Id));
    }

    [Fact]
    public void 信任记录损坏时按无记录处理而不抛异常()
    {
        WritePlugin();
        File.WriteAllText(TrustPath, "{ not json");

        Assert.Equal(PluginAvailability.Untrusted, Catalog().Availability(Id));
    }

    [Theory]
    [InlineData("ext.acme.demo", "..\\evil.exe")]
    [InlineData("ext.acme.demo", "C:\\Windows\\explorer.exe")]
    [InlineData("ext.acme.demo", "explorer.exe")]
    [InlineData("ext.acme.demo", "SvcHost.EXE")]
    [InlineData("ext.acme.demo", "notexe.dll")]
    public void 非法EXE名的manifest无效(string id, string exe)
    {
        WritePlugin(id, ManifestJson(id, exe));

        Assert.Equal(PluginAvailability.Invalid, Catalog().Availability(id));
    }

    [Fact]
    public void manifest的id与目录名不一致无效()
    {
        WritePlugin(Id, ManifestJson("ext.acme.other"));

        Assert.Equal(PluginAvailability.Invalid, Catalog().Availability(Id));
    }

    [Fact]
    public void 内置游戏id不能被插件占用()
    {
        WritePlugin("genshin", ManifestJson("genshin"));

        Assert.Equal(PluginAvailability.Invalid, Catalog().Availability("genshin"));
    }

    [Fact]
    public void ABI主版本过高被标记()
    {
        WritePlugin(manifest: ManifestJson(abi: 2));

        Assert.Equal(PluginAvailability.AbiTooNew, Catalog().Availability(Id));
    }

    [Fact]
    public void 缺少DLL或manifest无效()
    {
        var dir = WritePlugin();
        File.Delete(Path.Combine(dir, "module.dll"));

        Assert.Equal(PluginAvailability.Invalid, Catalog().Availability(Id));
    }

    [Fact]
    public void 未知能力无效()
    {
        WritePlugin(manifest: """{ "id": "ext.acme.demo", "displayName": "x", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": ["rootkit"] }""");

        Assert.Equal(PluginAvailability.Invalid, Catalog().Availability(Id));
    }

    [Fact]
    public void 符号链接目录被忽略()
    {
        var real = WritePlugin("ext.acme.real", ManifestJson("ext.acme.real"));
        var link = Path.Combine(PluginsDir, "ext.acme.link");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // 无权创建符号链接的环境跳过
        }

        Assert.DoesNotContain(Catalog().Scan(), entry => entry.Id == "ext.acme.link");
    }
}
