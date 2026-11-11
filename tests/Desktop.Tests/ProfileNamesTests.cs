using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;

namespace Desktop.Tests;

public sealed class ProfileNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-desktop-names-tests", Guid.NewGuid().ToString("N"));

    public ProfileNamesTests() => Directory.CreateDirectory(_dir);

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

    private ProfileManagerViewModel CreateManager(GameType scope = GameType.Genshin)
    {
        var data = new DesktopDataService(Path.Combine(_dir, "config.json"));
        data.Load();
        var manager = new ProfileManagerViewModel(data);
        manager.SetScope(scope);
        return manager;
    }

    [Fact]
    public void 默认名称依次取第一个没用过的()
    {
        Assert.Equal("国服", ProfileNames.NextDefault([]));
        Assert.Equal("国际服", ProfileNames.NextDefault(["国服"]));
        Assert.Equal("Steam", ProfileNames.NextDefault(["国服", "国际服"]));
        Assert.Equal("B 服", ProfileNames.NextDefault(["国服", "国际服", "Steam"]));
        Assert.Equal("配置 2", ProfileNames.NextDefault(["国服", "国际服", "Steam", "B 服"]));
        Assert.Equal("国际服", ProfileNames.NextDefault([" 国服 "]));
    }

    [Fact]
    public void 第一套占位配置和之后新建的配置按顺序命名()
    {
        var manager = CreateManager();
        Assert.Equal("国服", manager.SelectedProfile!.DisplayName);

        manager.NewCommand.Execute(null);
        Assert.Equal("国际服", manager.SelectedProfile!.DisplayName);

        manager.NewCommand.Execute(null);
        Assert.Equal("Steam", manager.SelectedProfile!.DisplayName);
    }

    [Fact]
    public void 不同游戏各自从国服开始命名()
    {
        var genshin = CreateManager(GameType.Genshin);
        genshin.NewCommand.Execute(null);

        var zzz = CreateManager(GameType.ZenlessZoneZero);

        Assert.Equal("国服", zzz.SelectedProfile!.DisplayName);
    }

    [Fact]
    public void 删掉一套后名称可以被再次使用()
    {
        var manager = CreateManager();
        manager.NewCommand.Execute(null);        // 国服 + 国际服
        manager.DeleteCommand.Execute(null);     // 删掉国际服

        manager.NewCommand.Execute(null);

        Assert.Equal("国际服", manager.SelectedProfile!.DisplayName);
    }
}
