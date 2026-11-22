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
    }

    [Fact]
    public async Task 已被其他配置使用的安装仍然列出并标注占用者()
    {
        var manager = CreateManager(TwoServers());
        var first = await manager.AutoFindExecutableAsync();
        manager.ApplyAutoFindChoice(first.Choices[0]); // 第一套拿到国服
        var firstName = manager.SelectedProfile!.DisplayName;
        manager.NewCommand.Execute(null);

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Equal(2, outcome.Choices.Count);
        Assert.Equal(GlobalExe, outcome.Choices[0].Path); // 没被占用的排前面，作为默认选中
        Assert.Null(outcome.Choices[0].Usage);
        Assert.Equal(CnExe, outcome.Choices[1].Path);
        Assert.Equal($"已用于「{firstName}」", outcome.Choices[1].Usage);
        Assert.Equal(string.Empty, manager.SelectedProfile!.ExecutablePath);
    }

    [Fact]
    public async Task 找到的都已被使用时也列出来不再提示没找到()
    {
        var manager = CreateManager(TwoServers());
        var first = await manager.AutoFindExecutableAsync();
        manager.ApplyAutoFindChoice(first.Choices[0]);
        manager.NewCommand.Execute(null);
        var second = await manager.AutoFindExecutableAsync();
        manager.ApplyAutoFindChoice(second.Choices[0]);
        manager.NewCommand.Execute(null);          // 第三套：两个安装都已被前两套用掉
        var third = manager.SelectedProfile!;

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Equal(2, outcome.Choices.Count);
        Assert.All(outcome.Choices, choice => Assert.StartsWith("已用于", choice.Usage));
        Assert.Equal(string.Empty, third.ExecutablePath);
    }

    [Fact]
    public async Task 只有一个且被其他配置使用时不直接填入()
    {
        var finder = new FakeFinder();
        finder.Hyp.Add(("nap_cn", "D:/Games/ZZZ-CN"));
        finder.Files.Add(CnExe);
        var manager = CreateManager(finder);
        var first = await manager.AutoFindExecutableAsync();   // 唯一且没被占用 → 直接填入
        Assert.Empty(first.Choices);
        Assert.Equal(CnExe, manager.SelectedProfile!.ExecutablePath);
        manager.NewCommand.Execute(null);

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Single(outcome.Choices);
        Assert.StartsWith("已用于", outcome.Choices[0].Usage);
        Assert.Equal(string.Empty, manager.SelectedProfile!.ExecutablePath);
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
    public async Task 当前配置自己的路径标注为当前并排在后面()
    {
        var manager = CreateManager(TwoServers());
        manager.SelectedProfile!.ExecutablePath = CnExe;

        var outcome = await manager.AutoFindExecutableAsync();

        Assert.Equal(2, outcome.Choices.Count);
        Assert.Equal(GlobalExe, outcome.Choices[0].Path);
        Assert.Equal(CnExe, outcome.Choices[1].Path);
        Assert.Equal("当前", outcome.Choices[1].Usage);
        Assert.Equal(CnExe, manager.SelectedProfile!.ExecutablePath);
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
