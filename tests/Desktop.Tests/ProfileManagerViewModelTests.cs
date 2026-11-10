using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Configuration;

namespace Desktop.Tests;

public sealed class ProfileManagerViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-desktop-tests", Guid.NewGuid().ToString("N"));

    public ProfileManagerViewModelTests() => Directory.CreateDirectory(_dir);

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

    private (DesktopDataService Data, ProfileManagerViewModel Manager) Create(GameType scope = GameType.Genshin)
    {
        var data = new DesktopDataService(ConfigPath);
        data.Load();
        var manager = new ProfileManagerViewModel(data);
        manager.SetScope(scope);
        return (data, manager);
    }

    private AppConfig Reload() => new AppConfigStore(ConfigPath).Load().Config;

    [Fact]
    public void 进入没有配置的游戏时自动落一个占位配置并保存()
    {
        var (_, manager) = Create(GameType.StarRail);

        Assert.Single(manager.Profiles);
        Assert.Single(Reload().Profiles, p => p.Game == GameType.StarRail);
    }

    [Fact]
    public void 修改字段后Flush立即写入config()
    {
        var (_, manager) = Create();
        manager.SelectedProfile!.TargetFps = 144;

        manager.Flush();

        Assert.Equal(144, Reload().Profiles.Single().Hoyo!.TargetFps);
    }

    [Fact]
    public void 添加配置立即保存并选中新配置()
    {
        var (_, manager) = Create();

        manager.NewCommand.Execute(null);

        Assert.Equal(2, manager.Profiles.Count);
        Assert.Equal(2, Reload().Profiles.Count(p => p.Game == GameType.Genshin));
        Assert.True(manager.HasMultiple);
        Assert.Equal(manager.Profiles[1].Id, manager.SelectedProfile!.Id);
    }

    [Fact]
    public void 复制配置带上全部设置并加副本后缀()
    {
        var (_, manager) = Create();
        manager.SelectedProfile!.TargetFps = 90;
        manager.Flush();

        manager.CopyCommand.Execute(null);

        var profiles = Reload().Profiles;
        Assert.Equal(2, profiles.Count);
        var copy = profiles.Single(p => p.DisplayName.EndsWith(" 副本", StringComparison.Ordinal));
        Assert.Equal(90, copy.Hoyo!.TargetFps);
    }

    [Fact]
    public void 删除当前配置后只剩另一套()
    {
        var (_, manager) = Create();
        manager.NewCommand.Execute(null);
        var removedId = manager.SelectedProfile!.Id;

        manager.DeleteCommand.Execute(null);

        Assert.DoesNotContain(Reload().Profiles, p => p.Id == removedId);
        Assert.Single(manager.Profiles);
    }

    [Fact]
    public void 保存被拒时报错且不改动已保存的配置()
    {
        var (_, manager) = Create();
        manager.SelectedProfile!.ExecutablePath = @"D:\Games\NotAGame.exe";

        manager.Flush();

        Assert.True(manager.HasStatus);
        Assert.Equal(string.Empty, Reload().Profiles.Single().ExecutablePath);
    }

    [Fact]
    public void 空白路径与NaN数值转换成不修改而不是抛异常()
    {
        var (_, manager) = Create();
        var editor = manager.SelectedProfile!;
        editor.ExecutablePath = "  ";
        editor.TargetFps = double.NaN;

        var mutation = editor.ToMutation();

        Assert.Null(mutation.ExecutablePath);
        Assert.Equal(120, mutation.TargetFps);
    }

    [Fact]
    public void 路径提示随输入变化()
    {
        var (_, manager) = Create();
        var editor = manager.SelectedProfile!;

        Assert.True(editor.HasPathHint); // 占位配置还没有路径

        editor.ExecutablePath = @"D:\Games\NotAGame.exe";
        Assert.Contains("不属于该游戏", editor.PathHint);
    }
}
