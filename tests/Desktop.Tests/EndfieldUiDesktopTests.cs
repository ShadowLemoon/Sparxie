using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Configuration;

namespace Desktop.Tests;

/// <summary>The settings page's view model for the Endfield touch interface: what the controls bind to and what reaches config.json.</summary>
public sealed class EndfieldUiDesktopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-desktop-tests", Guid.NewGuid().ToString("N"));

    public EndfieldUiDesktopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private ProfileManagerViewModel Create()
    {
        var data = new DesktopDataService(ConfigPath);
        data.Load();
        var manager = new ProfileManagerViewModel(data);
        manager.SetScope(GameType.Endfield);
        return manager;
    }

    private HoyoProfileSettings Saved() => new AppConfigStore(ConfigPath).Load().Config.Profiles.Single(p => p.Game == GameType.Endfield).Hoyo!;

    [Fact]
    public void 新配置默认缩放自动安全边距自动且滑块停在1_25()
    {
        var editor = Create().SelectedProfile!;

        Assert.Equal(0, editor.EndfieldUiScaleModeIndex);
        Assert.False(editor.EndfieldUiScaleManual);
        Assert.Equal(140, editor.EndfieldUiScalePercent);
        Assert.Equal("1.25×", editor.EndfieldUiScaleText);
        Assert.Equal(0, editor.EndfieldSafeAreaModeIndex);
        Assert.False(editor.EndfieldSafeManual);
    }

    [Fact]
    public void 手动缩放与手动边距每次改动立即写入配置()
    {
        var manager = Create();
        var editor = manager.SelectedProfile!;

        editor.EndfieldUiScaleModeIndex = 1;
        editor.EndfieldUiScalePercent = 150;
        editor.EndfieldSafeAreaModeIndex = 2;
        editor.EndfieldSafeLeft = 64;
        editor.EndfieldSafeRight = 48;

        var saved = Saved();
        Assert.Equal(("manual", 150, "manual", 64, 48), (saved.EndfieldUiScaleMode, saved.EndfieldUiScalePercent, saved.EndfieldSafeAreaMode, saved.EndfieldSafeLeft, saved.EndfieldSafeRight));
        Assert.True(editor.EndfieldUiScaleManual);
        Assert.True(editor.EndfieldSafeManual);
    }

    [Fact]
    public void 安全边距的三个选项对应自动_关闭_手动()
    {
        var editor = Create().SelectedProfile!;

        editor.EndfieldSafeAreaModeIndex = 1;
        Assert.Equal("off", Saved().EndfieldSafeAreaMode);
        Assert.False(editor.EndfieldSafeManual);

        editor.EndfieldSafeAreaModeIndex = 2;
        Assert.Equal("manual", Saved().EndfieldSafeAreaMode);

        editor.EndfieldSafeAreaModeIndex = 0;
        Assert.Equal("auto", Saved().EndfieldSafeAreaMode);
    }

    [Fact]
    public void 控件重建时的下标负一被忽略()
    {
        var editor = Create().SelectedProfile!;
        editor.EndfieldUiScaleModeIndex = 1;

        editor.EndfieldUiScaleModeIndex = -1;
        editor.EndfieldSafeAreaModeIndex = -1;

        Assert.Equal(1, editor.EndfieldUiScaleModeIndex);
        Assert.Equal(0, editor.EndfieldSafeAreaModeIndex);
    }

    [Fact]
    public void 滑块和数值框的越界值与空值被收回范围()
    {
        var editor = Create().SelectedProfile!;

        editor.EndfieldUiScalePercent = 400;
        Assert.Equal(180, editor.EndfieldUiScalePercent);
        editor.EndfieldUiScalePercent = 12;
        Assert.Equal(100, editor.EndfieldUiScalePercent);
        editor.EndfieldUiScalePercent = 133;
        Assert.Equal(135, editor.EndfieldUiScalePercent);
        editor.EndfieldSafeLeft = double.NaN;
        Assert.Equal(0, editor.EndfieldSafeLeft);
        editor.EndfieldSafeRight = 99999;
        Assert.Equal(600, editor.EndfieldSafeRight);
    }

    [Fact]
    public void 重新打开配置后控件读到保存的值()
    {
        var editor = Create().SelectedProfile!;
        editor.EndfieldUiScaleModeIndex = 1;
        editor.EndfieldUiScalePercent = 170;
        editor.EndfieldSafeAreaModeIndex = 2;
        editor.EndfieldSafeLeft = 30;

        var reopened = Create().SelectedProfile!;

        Assert.Equal((1, 170, 2, 30), (reopened.EndfieldUiScaleModeIndex, (int)reopened.EndfieldUiScalePercent, reopened.EndfieldSafeAreaModeIndex, (int)reopened.EndfieldSafeLeft));
    }

    [Fact]
    public void 复制配置带上触控界面设置()
    {
        var manager = Create();
        var editor = manager.SelectedProfile!;
        editor.EndfieldUiScaleModeIndex = 1;
        editor.EndfieldUiScalePercent = 145;
        editor.EndfieldSafeAreaModeIndex = 1;

        manager.CopyCommand.Execute(null);

        var copy = new AppConfigStore(ConfigPath).Load().Config.Profiles.Single(p => p.DisplayName.EndsWith(" 副本", StringComparison.Ordinal)).Hoyo!;
        Assert.Equal(("manual", 145, "off"), (copy.EndfieldUiScaleMode, copy.EndfieldUiScalePercent, copy.EndfieldSafeAreaMode));
    }
}
