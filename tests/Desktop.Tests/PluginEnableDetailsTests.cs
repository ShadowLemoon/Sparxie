using System.Text;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

public sealed class PluginEnableDetailsTests
{
    private static PluginEntry Entry(string capsJson, string extra = "", string dll = "aaaaaaaabbbbbbbbcc", string manifestHash = "m1")
    {
        var json = $$"""{ "id": "ext.acme.demo", "displayName": "演示", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": {{capsJson}}, "version": "1.2", "license": "MIT" {{extra}} }""";
        Assert.True(PluginManifest.TryParse(Encoding.UTF8.GetBytes(json), "ext.acme.demo", out var manifest, out var error), error);
        return new PluginEntry("ext.acme.demo", @"C:\p\ext.acme.demo", PluginAvailability.Untrusted, null, manifest, dll, manifestHash, 123);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("12345678", "12345678")]
    [InlineData("123456789", "12345678 9")]
    [InlineData("aaaaaaaabbbbbbbbcc", "aaaaaaaa bbbbbbbb cc")]
    public void 哈希按8位分组显示(string hash, string expected) => Assert.Equal(expected, PluginsViewModel.FormatHash(hash));

    [Fact]
    public void 首次启用_不是变化模式_注入类插件要求额外确认()
    {
        var details = PluginsViewModel.BuildEnableDetails(Entry("""["injects", "createsProcess"]"""), previous: null);

        Assert.False(details.IsChange);
        Assert.True(details.RequiresInjectionAck);
        Assert.Contains(details.RiskSentences, text => text.Contains("管理员权限"));
        Assert.Contains(details.RiskSentences, text => text.Contains("封号"));
        Assert.Contains("会注入游戏进程", details.Capabilities);
        Assert.Null(details.PreviousDllSha256);
    }

    [Fact]
    public void 非注入插件只要一个通用确认()
    {
        var details = PluginsViewModel.BuildEnableDetails(Entry("[]"), previous: null);

        Assert.False(details.RequiresInjectionAck);
        Assert.DoesNotContain(details.RiskSentences, text => text.Contains("封号"));
    }

    [Fact]
    public void 文件变化_带上上次确认的新旧哈希()
    {
        var previous = new PluginTrustRecord("ext.acme.demo", "oldoldold", "m1", 100, DateTimeOffset.UtcNow);

        var details = PluginsViewModel.BuildEnableDetails(Entry("[]"), previous);

        Assert.True(details.IsChange);
        Assert.Equal("oldoldold", details.PreviousDllSha256);
        Assert.Equal("aaaaaaaabbbbbbbbcc", details.DllSha256);
    }

    [Fact]
    public void 记录与当前一致时不算变化()
    {
        var previous = new PluginTrustRecord("ext.acme.demo", "aaaaaaaabbbbbbbbcc", "m1", 123, DateTimeOffset.UtcNow);

        Assert.False(PluginsViewModel.BuildEnableDetails(Entry("[]"), previous).IsChange);
    }

    [Fact]
    public void 能力标签包含needsArgs和注册表钩子()
    {
        var entry = Entry("""["needsArgs", "hotParams", "disableTouchUi"]""", """, "registryHooks": { "keys": ["Software\\Vendor\\Game"] }""");

        var labels = PluginsViewModel.CapabilityLabels(entry.Manifest!).ToList();

        Assert.Contains("需要启动参数", labels);
        Assert.Contains("支持热调", labels);
        Assert.Contains("支持关闭触屏界面", labels);
        Assert.Contains(labels, label => label.Contains("注册表"));
        Assert.Contains(PluginsViewModel.BuildEnableDetails(entry, null).RiskSentences, text => text.Contains("注册表") && text.Contains("恢复"));
    }
}
