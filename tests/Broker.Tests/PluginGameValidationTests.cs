using Sparxie.Broker.Validation;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Plugins;

namespace Broker.Tests;

/// <summary>Broker 对第三方游戏的校验：只放行用户已确认（Ready）的插件，EXE 白名单取 manifest。只读 manifest，不加载 DLL。</summary>
public sealed class PluginGameValidationTests : IDisposable
{
    private const string Id = "ext.acme.demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-broker-plugin-tests", Guid.NewGuid().ToString("N"));

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

    private CombinedGameCatalog Catalog(bool confirm)
    {
        var dir = Path.Combine(_root, "plugins", Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{Id}}", "displayName": "演示", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [] }""");
        File.WriteAllText(Path.Combine(dir, "module.dll"), "not-a-real-dll");
        var trust = new PluginTrustStore(Path.Combine(_root, "plugin-trust.json"));
        var plugins = new PluginCatalog(Path.Combine(_root, "plugins"), trust);
        if (confirm)
        {
            var entry = plugins.Scan().Single();
            trust.Confirm(new PluginTrustRecord(Id, entry.DllSha256!, entry.ManifestSha256!, entry.DllSize, DateTimeOffset.UtcNow));
        }

        return new CombinedGameCatalog(plugins);
    }

    private static ProfileSnapshot Snapshot(string exe = @"D:\Games\Demo.exe") => new()
    {
        ProfileId = "p1",
        DisplayName = "演示",
        Game = Id,
        ExecutablePath = exe,
    };

    [Fact]
    public void 已确认的插件游戏通过()
    {
        Assert.Null(ProfileSnapshotValidator.Validate(Snapshot(), Catalog(confirm: true)));
    }

    [Fact]
    public void 未确认的插件游戏被拒绝()
    {
        Assert.NotNull(ProfileSnapshotValidator.Validate(Snapshot(), Catalog(confirm: false)));
    }

    [Fact]
    public void 插件游戏的EXE不在manifest白名单时被拒绝()
    {
        Assert.NotNull(ProfileSnapshotValidator.Validate(Snapshot(@"D:\Games\Other.exe"), Catalog(confirm: true)));
    }

    [Fact]
    public void 不带目录时第三方id仍然被拒绝()
    {
        Assert.NotNull(ProfileSnapshotValidator.Validate(Snapshot()));
    }

    [Fact]
    public void 内置游戏在带插件目录时行为不变()
    {
        var snapshot = new ProfileSnapshot { ProfileId = "p", DisplayName = "星铁", Game = "starRail", ExecutablePath = @"D:\G\StarRail.exe" };
        var catalog = Catalog(confirm: true);

        Assert.Null(ProfileSnapshotValidator.Validate(snapshot, catalog));
        snapshot.ExecutablePath = @"D:\G\Demo.exe";
        Assert.NotNull(ProfileSnapshotValidator.Validate(snapshot, catalog));
    }
}
