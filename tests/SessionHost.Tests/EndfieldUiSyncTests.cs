using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Endfield;
using Sparxie.SessionHost.Sessions;

namespace SessionHost.Tests;

/// <summary>The Endfield touch-interface settings on their way from the profile to endfield-ui.ini, at launch and while the game runs.</summary>
public sealed class EndfieldUiSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-sh-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public EndfieldUiSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    private string Ini => Path.Combine(_dir, EndfieldUiIni.FileName);

    private string Config => Path.Combine(_dir, "config.json");

    private EndfieldUiSync Sync(string profileId = "endfield-1") => new(profileId, Ini, Config, _log.Add);

    private static string Profile(string id, string hoyo) =>
        "{ \"id\": \"" + id + "\", \"displayName\": \"" + id + "\", \"game\": \"endfield\", \"executablePath\": \"D:\\\\Games\\\\Endfield.exe\", \"hoyo\": { " + hoyo + " } }";

    private int _version;

    private void WriteConfig(params string[] profiles)
    {
        File.WriteAllText(Config, $$"""{ "schemaVersion": 1, "selectedProfileId": null, "profiles": [ {{string.Join(",\n", profiles)}} ] }""");
        // a save inside the timestamp granularity must still count as a change in the tests
        File.SetLastWriteTimeUtc(Config, new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc).AddSeconds(++_version * 7));
    }

    // ---- snapshot → model ----

    [Fact]
    public void 旧客户端不发新字段时是自动自动和默认值()
    {
        var model = new HoyoSettings { EndfieldInputMode = "touch", FpsUnlockEnabled = true, TargetFps = 120, EndfieldWidth = 1920, EndfieldHeight = 1080, EndfieldRefreshRate = 60 }.ToModel()!;

        Assert.Equal(("auto", 140, "auto", 0, 0), (model.EndfieldUiScaleMode, model.EndfieldUiScalePercent, model.EndfieldSafeAreaMode, model.EndfieldSafeLeft, model.EndfieldSafeRight));
        Assert.Equal(("auto", "auto"), EndfieldUiSettings.IniValues(model));
    }

    [Fact]
    public void 手动值进入模型并被限制在范围内()
    {
        var model = new HoyoSettings
        {
            EndfieldUiScaleMode = "manual", EndfieldUiScalePercent = 170, EndfieldSafeAreaMode = "manual", EndfieldSafeLeft = 60, EndfieldSafeRight = 9999,
        }.ToModel()!;

        Assert.Equal(("manual", 170, "manual", 60, 600), (model.EndfieldUiScaleMode, model.EndfieldUiScalePercent, model.EndfieldSafeAreaMode, model.EndfieldSafeLeft, model.EndfieldSafeRight));
        Assert.Equal(("1.70", "60,600"), EndfieldUiSettings.IniValues(model));
    }

    [Fact]
    public void 未知的方式文本按自动处理而不是拒绝()
    {
        var model = new HoyoSettings { EndfieldUiScaleMode = "turbo", EndfieldSafeAreaMode = "wide" }.ToModel()!;

        Assert.Equal(("auto", "auto"), EndfieldUiSettings.IniValues(model));
    }

    // ---- launch write ----

    [Fact]
    public void 启动前写入两行并记一行日志()
    {
        using var sync = Sync();

        Assert.True(sync.Write(new HoyoProfileSettings { EndfieldUiScaleMode = "manual", EndfieldUiScalePercent = 140, EndfieldSafeAreaMode = "off" }));

        Assert.Equal("ui_scale=1.40\r\nsafe_area=off\r\n", File.ReadAllText(Ini));
        Assert.Single(_log, line => line.Contains("ui_scale=1.40") && line.Contains("safe_area=off"));
    }

    [Fact]
    public void 启动写入保留Runtime自己的其他键()
    {
        File.WriteAllText(Ini, "# runtime\r\nobserve=1\r\nscale=1.25\r\nsafe_left=186\r\nsafe_right=186\r\n");
        using var sync = Sync();

        sync.Write(new HoyoProfileSettings());

        var text = File.ReadAllText(Ini);
        Assert.Equal("# runtime\r\nui_scale=auto\r\nsafe_area=auto\r\nobserve=1\r\nscale=1.25\r\nsafe_left=186\r\nsafe_right=186\r\n", text);
    }

    [Fact]
    public void ini不可写时记日志而不抛出()
    {
        Directory.CreateDirectory(Ini); // a directory where the file should be: Move fails
        using var sync = Sync();

        Assert.False(sync.Write(new HoyoProfileSettings()));

        Assert.Contains(_log, line => line.StartsWith("endfield-ui.ini not written"));
    }

    // ---- live ----

    [Fact]
    public void 游戏运行中保存配置后ini随之改变()
    {
        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"auto\""));
        using var sync = Sync();
        sync.Write(new HoyoProfileSettings());
        sync.Poll();
        _log.Clear();

        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 155, \"endfieldSafeAreaMode\": \"manual\", \"endfieldSafeLeft\": 44, \"endfieldSafeRight\": 33"));
        Assert.True(sync.Poll());

        Assert.Equal("ui_scale=1.55\r\nsafe_area=44,33\r\n", File.ReadAllText(Ini));
        Assert.False(sync.Poll(), "the same file is not applied twice");
    }

    [Fact]
    public void 别的Profile的改动不影响正在运行的这一个()
    {
        WriteConfig(
            Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 120"),
            Profile("endfield-2", "\"endfieldUiScaleMode\": \"auto\""));
        using var sync = Sync("endfield-1");
        sync.Poll();
        var before = File.ReadAllText(Ini);

        WriteConfig(
            Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 120"),
            Profile("endfield-2", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 180"));

        Assert.False(sync.Poll());
        Assert.Equal(before, File.ReadAllText(Ini));
    }

    [Fact]
    public void 配置损坏或Profile被删时ini保持原样且之后能恢复()
    {
        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 130"));
        using var sync = Sync();
        sync.Poll();
        var applied = File.ReadAllText(Ini);

        File.WriteAllText(Config, "{ not json");
        File.SetLastWriteTimeUtc(Config, new DateTime(2026, 11, 13, 2, 0, 0, DateTimeKind.Utc));
        Assert.False(sync.Poll());
        Assert.Equal(applied, File.ReadAllText(Ini));

        WriteConfig(Profile("other", "\"endfieldUiScaleMode\": \"auto\""));
        Assert.False(sync.Poll());
        Assert.Equal(applied, File.ReadAllText(Ini));

        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 175"));
        Assert.True(sync.Poll());
        Assert.StartsWith("ui_scale=1.75", File.ReadAllText(Ini));
    }

    [Fact]
    public void 配置里的坏值在读取时被修正后再写入ini()
    {
        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 99999, \"endfieldSafeAreaMode\": \"manual\", \"endfieldSafeLeft\": -3, \"endfieldSafeRight\": 8"));
        using var sync = Sync();

        Assert.True(sync.Poll());

        Assert.Equal("ui_scale=1.80\r\nsafe_area=0,8\r\n", File.ReadAllText(Ini));
    }

    [Fact]
    public void 后台轮询在会话期间跟随保存并在释放后停止()
    {
        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"auto\""));
        var sync = Sync();
        sync.Write(new HoyoProfileSettings());
        sync.Start(TimeSpan.FromMilliseconds(40));

        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 110"));
        Assert.True(SpinWait.SpinUntil(() => File.ReadAllText(Ini).StartsWith("ui_scale=1.10", StringComparison.Ordinal), TimeSpan.FromSeconds(5)));

        sync.Dispose();
        WriteConfig(Profile("endfield-1", "\"endfieldUiScaleMode\": \"manual\", \"endfieldUiScalePercent\": 160"));
        Thread.Sleep(300);
        Assert.StartsWith("ui_scale=1.10", File.ReadAllText(Ini));
    }

    [Fact]
    public async Task 控制器在创建游戏之前就写好ini即使启动随后失败()
    {
        // production flags check the game's signature, so this launch fails after the ini is in place: the order is what is shown
        var game = Path.Combine(_dir, "Endfield.exe");
        File.Copy(Environment.ProcessPath!, game);
        File.WriteAllText(Ini, "observe=1\r\n");
        using var controller = new EndfieldGameController(Ini, Config);
        var profile = new ProfileSnapshot { ProfileId = "endfield-1", DisplayName = "e", Game = "endfield", ExecutablePath = game };
        await controller.PrepareLaunchAsync(profile, CancellationToken.None);
        var hoyo = new HoyoProfileSettings { EndfieldUiScaleMode = "manual", EndfieldUiScalePercent = 140, EndfieldSafeAreaMode = "manual", EndfieldSafeLeft = 186, EndfieldSafeRight = 186 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.InstallAsync(null, IntPtr.Zero, hoyo, CancellationToken.None));

        Assert.Equal("ui_scale=1.40\r\nsafe_area=186,186\r\nobserve=1\r\n", File.ReadAllText(Ini));
    }
}
