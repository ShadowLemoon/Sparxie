using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;

namespace Desktop.Tests;

public sealed class ThemeSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-theme-" + Guid.NewGuid().ToString("N"));

    public ThemeSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("System", 0)]
    [InlineData("light", 1)]
    [InlineData("Dark", 2)]
    [InlineData("乱写", 0)]
    public void 主题字符串到下拉序号(string? value, int index) => Assert.Equal(index, ThemeNames.IndexOf(value));

    [Fact]
    public void 选择主题后写入config_重新打开仍生效()
    {
        var path = Path.Combine(_dir, "config.json");
        var data = new DesktopDataService(path);
        data.Load();
        var settings = new SettingsViewModel(data);

        settings.ThemeIndex = 1;

        Assert.Equal("Light", data.Preferences.Theme);
        var again = new DesktopDataService(path);
        again.Load();
        Assert.Equal(1, new SettingsViewModel(again).ThemeIndex);
        settings.ThemeIndex = -1; // 下拉框重建时回写 -1，忽略
        Assert.Equal(1, settings.ThemeIndex);
    }
}
