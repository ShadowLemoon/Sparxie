using System.Text;
using System.Text.Json.Nodes;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

/// <summary>设计文档 P1 / P2 的 ViewModel 与基础设施层行为。</summary>
public sealed class PluginP1Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-p1-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static PluginManifest Parse(string extra)
    {
        var json = $$"""{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["D.exe"], "caps": ["injects"] {{extra}} }""";
        Assert.True(PluginManifest.TryParse(Encoding.UTF8.GetBytes(json), "ext.acme.demo", out var manifest, out var error), error);
        return manifest;
    }

    [Fact]
    public void 信任记录新增的展示字段可往返_旧记录没有这些字段也能读()
    {
        Directory.CreateDirectory(_root);
        var store = new PluginTrustStore(Path.Combine(_root, "t.json"));
        store.Confirm(new PluginTrustRecord("ext.acme.a", "d", "m", 1, DateTimeOffset.UtcNow, "1.2", "演示", ["injects"]));
        var loaded = store.Find("ext.acme.a")!;
        Assert.Equal("1.2", loaded.Version);
        Assert.Equal(["injects"], loaded.Caps!);

        File.WriteAllText(Path.Combine(_root, "old.json"),
            """{ "version": 1, "records": [ { "id": "ext.acme.old", "dllSha256": "d", "manifestSha256": "m", "size": 1, "confirmedAt": "2026-01-01T00:00:00+00:00" } ] }""");
        var old = new PluginTrustStore(Path.Combine(_root, "old.json")).Find("ext.acme.old");
        Assert.NotNull(old);
        Assert.Null(old!.Version);
    }

    [Fact]
    public void manifest的作者自述字段被解析_并有长度和链接校验()
    {
        var manifest = Parse(""", "author": "Alice", "description": "d", "privacyNote": "不联网", "links": [ { "title": "主页", "url": "https://example.com" } ]""");
        Assert.Equal("Alice", manifest.Author);
        Assert.Single(manifest.Links!);

        Assert.False(PluginManifest.TryParse(Encoding.UTF8.GetBytes(
            """{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["D.exe"], "links": [ { "title": "x", "url": "http://insecure" } ] }"""), "ext.acme.demo", out _, out _));
        Assert.False(PluginManifest.TryParse(Encoding.UTF8.GetBytes(
            $$"""{ "id": "ext.acme.demo", "displayName": "D", "abiMajor": 1, "exeNames": ["D.exe"], "author": "{{new string('a', 65)}}" }"""), "ext.acme.demo", out _, out _));
    }

    [Fact]
    public void 作者自述和版本差异进入确认框数据()
    {
        var manifest = Parse(""", "version": "2.0", "author": "Alice", "links": [ { "title": "主页", "url": "https://example.com" } ]""");
        var entry = new PluginEntry("ext.acme.demo", @"C:\p", PluginAvailability.HashChanged, null, manifest, "new", "m2", 5);
        var previous = new PluginTrustRecord("ext.acme.demo", "old", "m1", 5, DateTimeOffset.UtcNow, "1.0", "D", []);

        var details = PluginsViewModel.BuildEnableDetails(entry, previous);

        Assert.Equal("Alice", details.Author);
        Assert.Equal("1.0", details.PreviousVersion);
        Assert.Contains("injects", details.AddedCapabilities!);
        Assert.Equal("https://example.com", details.Links!.Single().Value);
    }

    private static PluginSettingsSchema Schema(string json)
    {
        Assert.True(PluginSettingsSchema.TryParse(json, out var schema, out var error), error);
        return schema;
    }

    [Fact]
    public void 字段分组被解析_恢复默认只恢复被改过的字段()
    {
        var schema = Schema("""
            { "fields": [
              { "key": "a", "label": "A", "type": "int", "default": 10, "min": 0, "max": 100, "group": "界面" },
              { "key": "b", "label": "B", "type": "bool", "default": true, "group": "界面" },
              { "key": "c", "label": "C", "type": "string", "default": "x", "group": "高级" } ] }
            """);
        Assert.Equal(["界面", "界面", "高级"], schema.Fields.Select(field => field.Group).ToArray());
        var changed = 0;
        var fields = schema.Fields.Select(field => new PluginFieldViewModel(field, null, () => changed++)).ToList();
        Assert.All(fields, field => Assert.True(field.IsDefault));

        fields[0].NumberValue = 50;
        fields[2].TextValue = "y";

        Assert.False(fields[0].IsDefault);
        Assert.True(fields[0].CanReset);
        Assert.True(fields[1].IsDefault);

        foreach (var field in fields)
        {
            field.ResetToDefault();
        }

        Assert.All(fields, field => Assert.True(field.IsDefault));
        Assert.Equal(10, (int)fields[0].NumberValue);
        Assert.Equal("x", fields[2].TextValue);
    }

    [Fact]
    public void 编辑器只在每组第一个字段上放分组标题()
    {
        var schema = Schema("""
            { "fields": [
              { "key": "a", "label": "A", "type": "bool", "default": true, "group": "界面" },
              { "key": "b", "label": "B", "type": "bool", "default": true, "group": "界面" },
              { "key": "c", "label": "C", "type": "bool", "default": true } ] }
            """);
        Assert.Equal(["界面", "界面", null], schema.Fields.Select(field => field.Group).ToArray());
        _ = new JsonObject();
    }
}
