using System.Text.Json.Nodes;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>
/// 插件设置 schema：解析校验与"全函数"归一化。示例 schema 取自一类典型需求（输入模式、是否跟随串流分辨率、宽高刷新率、
/// 界面缩放 100–180 步长 5、安全区 0–600 像素），全部用通用字段名表达。
/// </summary>
public sealed class PluginSettingsSchemaTests
{
    public const string Sample = """
        { "fields": [
          { "key": "inputMode", "label": "输入模式", "type": "enum", "default": "touch",
            "options": [ { "value": "touch", "label": "触屏" }, { "value": "pc", "label": "键鼠" } ] },
          { "key": "autoResolution", "label": "分辨率跟随串流", "type": "bool", "default": true },
          { "key": "width", "label": "宽度", "type": "int", "min": 320, "max": 7680, "default": 1920,
            "showWhen": { "key": "autoResolution", "equals": false } },
          { "key": "height", "label": "高度", "type": "int", "min": 240, "max": 4320, "default": 1080,
            "showWhen": { "key": "autoResolution", "equals": false } },
          { "key": "uiScale", "label": "界面缩放（%）", "type": "int", "min": 100, "max": 180, "step": 5, "default": 140, "hint": "100–180，步长 5" },
          { "key": "safeLeft", "label": "左安全区", "type": "int", "min": 0, "max": 600, "default": 0 },
          { "key": "note", "label": "备注", "type": "string", "default": "", "maxLength": 20 }
        ] }
        """;

    private static PluginSettingsSchema Parse(string json)
    {
        Assert.True(PluginSettingsSchema.TryParse(json, out var schema, out var error), error);
        return schema;
    }

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void 没有schema等于没有设置项()
    {
        Assert.True(PluginSettingsSchema.TryParse(null, out var schema, out _));
        Assert.Empty(schema.Fields);
        Assert.Empty(schema.Normalize(Obj("""{ "x": 1 }""")));
    }

    [Fact]
    public void 示例schema可解析且字段属性正确()
    {
        var schema = Parse(Sample);

        Assert.Equal(7, schema.Fields.Count);
        var width = schema.Fields.Single(f => f.Key == "width");
        Assert.Equal(PluginFieldKind.Int, width.Kind);
        Assert.Equal((320, 7680), (width.Min, width.Max));
        Assert.Equal("autoResolution", width.ShowWhen!.Key);
        Assert.Equal(5, schema.Fields.Single(f => f.Key == "uiScale").Step);
        Assert.Equal(2, schema.Fields.Single(f => f.Key == "inputMode").Options.Count);
    }

    [Fact]
    public void 空设置归一化为全部默认值()
    {
        var result = Parse(Sample).Normalize(null);

        Assert.Equal("touch", (string)result["inputMode"]!);
        Assert.True((bool)result["autoResolution"]!);
        Assert.Equal(1920, (int)result["width"]!);
        Assert.Equal(140, (int)result["uiScale"]!);
        Assert.Equal(string.Empty, (string)result["note"]!);
    }

    [Fact]
    public void 数值夹到范围并对齐步长()
    {
        var schema = Parse(Sample);

        Assert.Equal(180, (int)schema.Normalize(Obj("""{ "uiScale": 9999 }"""))["uiScale"]!);
        Assert.Equal(100, (int)schema.Normalize(Obj("""{ "uiScale": -5 }"""))["uiScale"]!);
        Assert.Equal(135, (int)schema.Normalize(Obj("""{ "uiScale": 133 }"""))["uiScale"]!); // 对齐到 5 的倍数（以 min 为起点）
        Assert.Equal(320, (int)schema.Normalize(Obj("""{ "width": 1 }"""))["width"]!);
        Assert.Equal(7680, (int)schema.Normalize(Obj("""{ "width": 100000 }"""))["width"]!);
        Assert.Equal(1280, (int)schema.Normalize(Obj("""{ "width": 1279.6 }"""))["width"]!); // 浮点取整
    }

    [Fact]
    public void 类型不对_枚举不在选项里_未知键_都不抛异常而是回落默认值()
    {
        var result = Parse(Sample).Normalize(Obj("""
            { "inputMode": "gamepad", "autoResolution": "yes", "width": "wide", "uiScale": null, "safeLeft": [1], "note": 5, "evil": 1 }
            """));

        Assert.Equal("touch", (string)result["inputMode"]!);
        Assert.True((bool)result["autoResolution"]!);
        Assert.Equal(1920, (int)result["width"]!);
        Assert.Equal(140, (int)result["uiScale"]!);
        Assert.Equal(0, (int)result["safeLeft"]!);
        Assert.Equal(string.Empty, (string)result["note"]!);
        Assert.False(result.ContainsKey("evil")); // 未知键被丢弃
    }

    [Fact]
    public void 字符串被截断并去掉控制字符()
    {
        var result = Parse(Sample).Normalize(Obj("""{ "note": "  hello\u0000\u0007world, this is too long to keep  " }"""));

        var note = (string)result["note"]!;
        Assert.True(note.Length <= 20);
        Assert.DoesNotContain(note, c => char.IsControl(c));
        Assert.StartsWith("helloworld", note);
    }

    [Fact]
    public void 合法值原样保留()
    {
        var result = Parse(Sample).Normalize(Obj("""
            { "inputMode": "pc", "autoResolution": false, "width": 2560, "height": 1440, "uiScale": 150, "safeLeft": 186 }
            """));

        Assert.Equal("pc", (string)result["inputMode"]!);
        Assert.False((bool)result["autoResolution"]!);
        Assert.Equal(2560, (int)result["width"]!);
        Assert.Equal(150, (int)result["uiScale"]!);
        Assert.Equal(186, (int)result["safeLeft"]!);
    }

    [Fact]
    public void showWhen按当前值决定字段是否可见()
    {
        var schema = Parse(Sample);
        var width = schema.Fields.Single(f => f.Key == "width");

        Assert.False(PluginSettingsSchema.IsVisible(width, Obj("""{ "autoResolution": true }""")));
        Assert.True(PluginSettingsSchema.IsVisible(width, Obj("""{ "autoResolution": false }""")));
        Assert.True(PluginSettingsSchema.IsVisible(schema.Fields.Single(f => f.Key == "uiScale"), Obj("{}")));
    }

    [Theory]
    [InlineData("""{ "fields": "x" }""")]
    [InlineData("""{ "nofields": [] }""")]
    [InlineData("""{ "fields": [ { "key": "1bad", "label": "x", "type": "bool", "default": true } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "bool", "default": true }, { "key": "a", "label": "y", "type": "bool", "default": false } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "float", "default": 1 } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "", "type": "bool", "default": true } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "int", "min": 10, "max": 5, "default": 7 } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "int", "min": 0, "max": 10, "default": 99 } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "int", "min": 0, "max": 10, "default": 5, "step": 0 } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "enum", "default": "z", "options": [ { "value": "a", "label": "A" } ] } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "enum", "default": "a", "options": [] } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "bool", "default": true, "showWhen": { "key": "later", "equals": true } }, { "key": "later", "label": "y", "type": "bool", "default": true } ] }""")]
    [InlineData("""{ "fields": [ { "key": "a", "label": "x", "type": "string", "default": "toolongvalue", "maxLength": 3 } ] }""")]
    [InlineData("{ not json")]
    public void 写错的schema被拒绝(string json)
    {
        Assert.False(PluginSettingsSchema.TryParse(json, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void manifest带非法schema时整个插件无效_带合法schema时可用()
    {
        string Manifest(string schema) => $$"""{ "id": "ext.acme.demo", "displayName": "x", "abiMajor": 1, "exeNames": ["D.exe"], "settingsSchema": {{schema}} }""";

        Assert.False(PluginManifest.TryParse(System.Text.Encoding.UTF8.GetBytes(Manifest("""{ "fields": 3 }""")), "ext.acme.demo", out _, out var error));
        Assert.Contains("settingsSchema", error);

        Assert.True(PluginManifest.TryParse(System.Text.Encoding.UTF8.GetBytes(Manifest(Sample)), "ext.acme.demo", out var manifest, out _));
        Assert.Equal(7, manifest.Schema.Fields.Count);
    }

    [Fact]
    public void ParseObject对空_损坏_非对象_过大都返回null()
    {
        Assert.Null(PluginSettingsSchema.ParseObject(null));
        Assert.Null(PluginSettingsSchema.ParseObject(""));
        Assert.Null(PluginSettingsSchema.ParseObject("{ broken"));
        Assert.Null(PluginSettingsSchema.ParseObject("[1,2]"));
        Assert.Null(PluginSettingsSchema.ParseObject(new string(' ', PluginSettingsSchema.MaxSettingsBytes + 1)));
        Assert.NotNull(PluginSettingsSchema.ParseObject("""{ "a": 1 }"""));
    }
}
