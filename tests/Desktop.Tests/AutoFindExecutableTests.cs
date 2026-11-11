using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Processes;

namespace Desktop.Tests;

public sealed class AutoFindExecutableTests : IDisposable
{
    private sealed class FakeFinder : IGameFinderEnvironment
    {
        public List<(string Biz, string Directory)> Hyp { get; } = [];

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories() => Hyp;

        public IEnumerable<(string Name, string Directory)> UninstallInstallDirectories() => [];

        public IEnumerable<string> RecentExecutablePaths() => [];

        public IEnumerable<string> RunningExecutablePaths(string executableName) => [];

        public bool FileExists(string path) => Files.Contains(path);

        public IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth) => [];
    }

    private const string CnExe = @"D:\Games\ZZZ-CN\ZenlessZoneZero.exe";
    private const string GlobalExe = @"D:\Games\ZZZ-Global\ZenlessZoneZero.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-desktop-find-tests", Guid.NewGuid().ToString("N"));

    public AutoFindExecutableTests() => Directory.CreateDirectory(_dir);

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

    private static FakeFinder TwoServers()
    {
        var finder = new FakeFinder();
        finder.Hyp.Add(("nap_global", "D:/Games/ZZZ-Global"));
        finder.Hyp.Add(("nap_cn", "D:/Games/ZZZ-CN"));
        finder.Files.Add(CnExe);
        finder.Files.Add(GlobalExe);
        return finder;
    }

    private ProfileManagerViewModel CreateManager(IGameFinderEnvironment finder)
    {
        var data = new DesktopDataService(Path.Combine(_dir, "config.json"));
        data.Load();
        var manager = new ProfileManagerViewModel(data, finder);
        manager.SetScope(GameType.ZenlessZoneZero);
        return manager;
    }

    [Fact]
    public async Task 找到多个时不自动填入而是返回候选交给界面选择()
    {
        var manager = CreateManager(TwoServers());

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Equal(2, outcome.Choices.Count);
        Assert.Equal(CnExe, outcome.Choices[0].Path); // 国服排在前面，作为弹窗的默认选中
        Assert.Equal(GlobalExe, outcome.Choices[1].Path);
        Assert.Equal(string.Empty, manager.SelectedProfile!.ExecutablePath);
    }

    [Fact]
    public async Task 选择候选后填入路径并立即保存()
    {
        var manager = CreateManager(TwoServers());
        var outcome = await manager.AutoFindExecutableAsync();

        manager.ApplyAutoFindChoice(outcome.Choices[1]);

        Assert.Equal(GlobalExe, manager.SelectedProfile!.ExecutablePath);
        Assert.Contains("已填入", manager.StatusMessage);
    }

    [Fact]
    public async Task 只剩一个没用过的时直接填入不弹窗()
    {
        var manager = CreateManager(TwoServers());
        var first = await manager.AutoFindExecutableAsync();
        manager.ApplyAutoFindChoice(first.Choices[0]); // 第一套拿到国服
        manager.NewCommand.Execute(null);

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Empty(outcome.Choices);
        Assert.Equal(GlobalExe, manager.SelectedProfile!.ExecutablePath);
        Assert.Contains("已填入", outcome.Message);
    }

    [Fact]
    public async Task 找到的都已添加时提示没找到且不改路径()
    {
        var manager = CreateManager(TwoServers());
        var first = await manager.AutoFindExecutableAsync();
        manager.ApplyAutoFindChoice(first.Choices[0]);
        manager.NewCommand.Execute(null);
        await manager.AutoFindExecutableAsync();   // 第二套自动拿到国际服
        manager.NewCommand.Execute(null);          // 第三套：两个安装都已被前两套用掉
        var third = manager.SelectedProfile!;

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Empty(outcome.Choices);
        Assert.Equal(string.Empty, third.ExecutablePath);
        Assert.Contains("没有找到新的", outcome.Message);
        Assert.Contains("已经添加", outcome.Message);
    }

    [Fact]
    public async Task 什么都没找到时提示手动选择()
    {
        var manager = CreateManager(new FakeFinder());

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Equal(string.Empty, manager.SelectedProfile!.ExecutablePath);
        Assert.Contains("没有找到", outcome.Message);
        Assert.Contains("手动", outcome.Message);
    }

    [Fact]
    public async Task 当前配置自己已有的路径不再作为候选()
    {
        var manager = CreateManager(TwoServers());
        manager.SelectedProfile!.ExecutablePath = CnExe;

        var outcome = await manager.AutoFindExecutableAsync();

        // 国服已经是这套配置自己的路径 → 只剩国际服，直接填入
        Assert.Empty(outcome.Choices);
        Assert.Equal(GlobalExe, manager.SelectedProfile!.ExecutablePath);
    }

    [Fact]
    public async Task 查找结果提示在界面上可见()
    {
        var manager = CreateManager(new FakeFinder());

        await manager.AutoFindExecutableAsync();

        Assert.True(manager.HasStatus);
        Assert.Contains("没有找到", manager.StatusMessage);
    }
}
