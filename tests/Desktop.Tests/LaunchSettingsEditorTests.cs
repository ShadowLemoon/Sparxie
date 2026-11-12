using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Configuration;

namespace Desktop.Tests;

public sealed class LaunchSettingsEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-desktop-launch-tests", Guid.NewGuid().ToString("N"));

    public LaunchSettingsEditorTests() => Directory.CreateDirectory(_dir);

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

    // 下拉列表随运行机器的主显示器变化（测试宿主可能只有很小的虚拟屏幕），测试一律取列表里最后（最小）的一项，不依赖具体分辨率。
    private static int LastIndex => ProfileEditorViewModel.ResolutionList.Length - 1;

    private static (int Width, int Height) Smallest => ProfileEditorViewModel.ResolutionList[LastIndex];

    private ProfileManagerViewModel CreateManager(GameType scope = GameType.Genshin)
    {
        var data = new DesktopDataService(ConfigPath);
        data.Load();
        var manager = new ProfileManagerViewModel(data);
        manager.SetScope(scope);
        return manager;
    }

    [Fact]
    public void 分辨率下拉包含主显示器且始终钉上4K_2K_1080p()
    {
        var list = ProfileEditorViewModel.BuildResolutionList((1920, 1200));

        Assert.Contains((1920, 1200), list);          // 主显示器本身
        Assert.Contains((3840, 2160), list);          // 钉住的 4K / 2K / 1080p，不管显示器多小
        Assert.Contains((2560, 1440), list);
        Assert.Contains((1920, 1080), list);
        Assert.DoesNotContain((2560, 1600), list);    // 其余超过显示器的仍然过滤
        Assert.Contains((640, 480), list);
        Assert.Equal(list.OrderByDescending(i => i.Width).ThenByDescending(i => i.Height), list);
        Assert.Equal(list.Length, list.Distinct().Count());
    }

    [Fact]
    public void 默认没有启动设置且命令行预览为空提示()
    {
        var editor = CreateManager().SelectedProfile!;

        Assert.True(editor.ToMutation().Launch!.IsEmpty);
        Assert.Equal(string.Empty, editor.CommandLinePreview);
        Assert.False(editor.OverrideResolution);
    }

    [Fact]
    public void 总开关关闭时不写分辨率与全屏只带无边框()
    {
        var editor = CreateManager().SelectedProfile!;
        editor.Fullscreen = true;
        editor.Borderless = true;

        var launch = editor.ToMutation().Launch!;

        Assert.Null(launch.Width);
        Assert.Null(launch.Fullscreen);
        Assert.True(launch.Borderless);
        Assert.Equal("-popupwindow", editor.CommandLinePreview);
    }

    [Fact]
    public void 总开关关闭时分辨率相关控件联动禁用_打开后可用()
    {
        var editor = CreateManager().SelectedProfile!;
        Assert.False(editor.OverrideResolution);
        Assert.False(editor.ResolutionControlsEnabled);
        Assert.False(editor.PresetEnabled);
        Assert.False(editor.CustomEnabled);

        editor.OverrideResolution = true;

        Assert.True(editor.ResolutionControlsEnabled);
        Assert.True(editor.PresetEnabled);
        editor.CustomResolution = true;
        Assert.True(editor.CustomEnabled);
        Assert.False(editor.PresetEnabled);
    }

    [Fact]
    public void 手动关掉总开关后不再带分辨率与全屏()
    {
        var editor = CreateManager().SelectedProfile!;
        editor.OverrideResolution = true;
        editor.Fullscreen = false;

        editor.OverrideResolution = false;

        var launch = editor.ToMutation().Launch;
        Assert.True(launch is null || (launch.Width is null && launch.Fullscreen is null));
    }

    [Fact]
    public void 总开关开启后用下拉里的分辨率与全屏()
    {
        var editor = CreateManager().SelectedProfile!;
        editor.OverrideResolution = true;
        editor.Fullscreen = false;
        editor.ResolutionIndex = LastIndex;

        var launch = editor.ToMutation().Launch!;

        Assert.Equal(Smallest.Width, launch.Width);
        Assert.Equal(Smallest.Height, launch.Height);
        Assert.Equal(false, launch.Fullscreen);
        Assert.True(editor.PresetEnabled);
        Assert.False(editor.CustomEnabled);
    }

    [Fact]
    public void 自定义分辨率启用输入框且NaN时不设分辨率()
    {
        var editor = CreateManager().SelectedProfile!;
        editor.OverrideResolution = true;
        editor.CustomResolution = true;
        editor.CustomWidth = 2000;
        editor.CustomHeight = 1200;
        editor.ExtraArguments = "  -foo  ";

        var launch = editor.ToMutation().Launch!;
        Assert.Equal(2000, launch.Width);
        Assert.Equal(1200, launch.Height);
        Assert.Equal("-foo", launch.ExtraArguments);
        Assert.True(editor.CustomEnabled);
        Assert.False(editor.PresetEnabled);

        editor.CustomWidth = double.NaN;
        Assert.Null(editor.ToMutation().Launch!.Width);
    }

    [Fact]
    public void 修改启动设置后自动保存并能从配置恢复到界面()
    {
        var manager = CreateManager();
        var editor = manager.SelectedProfile!;
        editor.OverrideResolution = true;
        editor.Fullscreen = false;
        editor.Borderless = true;
        editor.ResolutionIndex = LastIndex;
        editor.ExtraArguments = "-x";
        manager.Flush();

        var saved = new AppConfigStore(ConfigPath).Load().Config.Profiles.Single().Launch!;
        Assert.Equal(false, saved.Fullscreen);
        Assert.True(saved.Borderless);
        Assert.Equal(Smallest.Width, saved.Width);
        Assert.Equal("-x", saved.ExtraArguments);

        var restored = CreateManager().SelectedProfile!;
        Assert.True(restored.OverrideResolution);
        Assert.False(restored.Fullscreen);
        Assert.True(restored.Borderless);
        Assert.Equal(Smallest, ProfileEditorViewModel.ResolutionList[restored.ResolutionIndex]);
        Assert.Equal("-x", restored.ExtraArguments);
    }

    [Fact]
    public void 非列表分辨率恢复成自定义()
    {
        var manager = CreateManager();
        manager.SelectedProfile!.OverrideResolution = true;
        manager.SelectedProfile.CustomResolution = true;
        manager.SelectedProfile.CustomWidth = 1366;
        manager.SelectedProfile.CustomHeight = 768;
        manager.Flush();

        var restored = CreateManager().SelectedProfile!;

        Assert.True(restored.CustomResolution);
        Assert.Equal(1366, restored.CustomWidth);
        Assert.Equal(768, restored.CustomHeight);
    }

    [Fact]
    public void 全部关闭后配置里不再保存启动设置()
    {
        var manager = CreateManager();
        manager.SelectedProfile!.Borderless = true;
        manager.Flush();
        Assert.NotNull(new AppConfigStore(ConfigPath).Load().Config.Profiles.Single().Launch);

        manager.SelectedProfile.Borderless = false;
        manager.Flush();

        Assert.Null(new AppConfigStore(ConfigPath).Load().Config.Profiles.Single().Launch);
    }

    [Fact]
    public void 绝区零同样可以设置启动设置()
    {
        var manager = CreateManager(GameType.ZenlessZoneZero);
        manager.SelectedProfile!.Borderless = true;
        manager.Flush();

        var launch = new AppConfigStore(ConfigPath).Load().Config.Profiles.Single(p => p.Game == GameType.ZenlessZoneZero).Launch!;
        Assert.True(launch.Borderless);
    }
}
