using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>信任记录内容异常时必须失败为"不信任"，而不是让扫描（设置页构造时就会扫）抛异常。</summary>
public sealed class PluginTrustHardeningTests : IDisposable
{
    private const string Id = "ext.acme.demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-trust-hardening", Guid.NewGuid().ToString("N"));
    private string TrustPath => Path.Combine(_root, "plugin-trust.json");

    public PluginTrustHardeningTests() => Directory.CreateDirectory(Path.Combine(_root, "plugins", Id));

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

    private PluginCatalog Catalog()
    {
        var dir = Path.Combine(_root, "plugins", Id);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{Id}}", "displayName": "x", "abiMajor": 1, "exeNames": ["D.exe"], "caps": [] }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), "bytes");
        return new PluginCatalog(Path.Combine(_root, "plugins"), new PluginTrustStore(TrustPath));
    }

    [Theory]
    [InlineData("""{ "version": 1, "records": [ null ] }""")]
    [InlineData("""{ "version": 1, "records": [ { "id": null, "dllSha256": "a", "manifestSha256": "b", "size": 1 } ] }""")]
    [InlineData("""{ "version": 1, "records": [ { "id": "ext.acme.demo", "dllSha256": null, "manifestSha256": "b", "size": 1 } ] }""")]
    [InlineData("""{ "version": 1, "records": null }""")]
    [InlineData("""[]""")]
    [InlineData("""{ "version": 1, "records": [ { "id": "ext.acme.demo", "dllSha256": "a", "manifestSha256": "b", "size": "oops" } ] }""")]
    public void 异常的信任记录按未信任处理且不抛异常(string json)
    {
        File.WriteAllText(TrustPath, json);
        var catalog = Catalog();

        var entry = Assert.Single(catalog.Scan());

        Assert.Equal(PluginAvailability.Untrusted, entry.Availability);
        Assert.Empty(catalog.All);
    }

    [Fact]
    public void 重复id的记录只保留最后一条且不抛异常()
    {
        File.WriteAllText(TrustPath, """
            { "version": 1, "records": [
              { "id": "ext.acme.demo", "dllSha256": "aaa", "manifestSha256": "bbb", "size": 1, "confirmedAt": "2026-01-01T00:00:00Z" },
              { "id": "ext.acme.demo", "dllSha256": "ccc", "manifestSha256": "ddd", "size": 2, "confirmedAt": "2026-01-02T00:00:00Z" } ] }
            """);
        var store = new PluginTrustStore(TrustPath);

        var records = store.Load();

        Assert.Equal("ccc", Assert.Single(records).DllSha256);
        Assert.Equal(PluginAvailability.HashChanged, Catalog().Scan().Single().Availability);
    }

    [Fact]
    public void 插件根目录不存在或读取失败时扫描返回空()
    {
        var catalog = new PluginCatalog(Path.Combine(_root, "no-such-dir"), new PluginTrustStore(TrustPath));

        Assert.Empty(catalog.Scan());
    }
}
