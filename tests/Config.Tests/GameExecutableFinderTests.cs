using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

public class GameExecutableFinderTests
{
    private sealed class FakeEnvironment : IGameFinderEnvironment
    {
        public List<(string Biz, string Directory)> Hyp { get; } = [];

        public List<(string Name, string Directory)> Uninstall { get; } = [];

        public Dictionary<string, List<string>> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories() => Hyp;

        public IEnumerable<(string Name, string Directory)> UninstallInstallDirectories() => Uninstall;

        public List<string> Recent { get; } = [];

        public IEnumerable<string> RecentExecutablePaths() => Recent;

        public IEnumerable<string> RunningExecutablePaths(string executableName) =>
            Running.TryGetValue(executableName, out var list) ? list : [];

        public bool FileExists(string path) => Files.Contains(path);

        public IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth) =>
            Files.Where(f => f.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private const string CnExe = @"D:\Games\ZZZ-CN\ZenlessZoneZero.exe";
    private const string GlobalExe = @"D:\Games\ZZZ-Global\ZenlessZoneZero.exe";

    private static FakeEnvironment TwoServers()
    {
        var env = new FakeEnvironment();
        env.Hyp.Add(("nap_global", "D:/Games/ZZZ-Global"));
        env.Hyp.Add(("nap_cn", "D:/Games/ZZZ-CN"));
        env.Files.Add(CnExe);
        env.Files.Add(GlobalExe);
        return env;
    }

    [Fact]
    public void 启动器登记的安装按国服优先排序()
    {
        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, TwoServers());

        Assert.Equal([CnExe, GlobalExe], found.Select(c => c.Path));
        Assert.Contains("国服", found[0].Source);
    }

    [Fact]
    public void 选取时跳过已被现有配置使用的路径()
    {
        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, TwoServers());

        var pick = GameExecutableFinder.PickUnused(found, [CnExe]);

        Assert.Equal(GlobalExe, pick!.Path);
    }

    [Fact]
    public void 路径比较忽略大小写与斜杠方向()
    {
        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, TwoServers());

        var pick = GameExecutableFinder.PickUnused(found, ["d:/games/zzz-cn/zenlesszonezero.exe"]);

        Assert.Equal(GlobalExe, pick!.Path);
    }

    [Fact]
    public void 找到的都已添加时选取返回空()
    {
        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, TwoServers());

        Assert.NotEmpty(found);
        Assert.Null(GameExecutableFinder.PickUnused(found, [CnExe, GlobalExe, null, ""]));
    }

    [Fact]
    public void 没有任何登记时找不到()
    {
        Assert.Empty(GameExecutableFinder.Find(GameType.StarRail, new FakeEnvironment()));
    }

    [Fact]
    public void 登记路径下文件不存在或文件名不在白名单时忽略()
    {
        var env = new FakeEnvironment();
        env.Hyp.Add(("hkrpg_cn", "E:/Games/SR"));        // 目录里没有 StarRail.exe
        env.Hyp.Add(("hk4e_cn", "E:/Games/Genshin"));    // 别的游戏的登记
        env.Files.Add(@"E:\Games\Genshin\YuanShen.exe");

        Assert.Empty(GameExecutableFinder.Find(GameType.StarRail, env));
    }

    [Fact]
    public void 启动器安装目录下的游戏子目录能被找到()
    {
        var env = new FakeEnvironment();
        env.Uninstall.Add(("HYP_standalone_1_5_nap_global_Abc", @"D:\Steam\Zenless Zone Zero"));
        env.Uninstall.Add(("SomethingElse", @"D:\Steam\Other"));
        env.Files.Add(@"D:\Steam\Zenless Zone Zero\games\ZenlessZoneZero Game\ZenlessZoneZero.exe");
        env.Files.Add(@"D:\Steam\Other\ZenlessZoneZero.exe");  // 不属于这款游戏的卸载项，不应被扫描

        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, env);

        var single = Assert.Single(found);
        Assert.EndsWith(@"ZenlessZoneZero Game\ZenlessZoneZero.exe", single.Path);
        Assert.Equal(3, single.Priority);
    }

    [Fact]
    public void 正在运行的游戏也是候选且同一路径只出现一次()
    {
        var env = TwoServers();
        env.Running["ZenlessZoneZero"] = [GlobalExe, @"D:\Elsewhere\ZenlessZoneZero.exe"];
        env.Files.Add(@"D:\Elsewhere\ZenlessZoneZero.exe");

        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, env);

        Assert.Equal(3, found.Count);
        Assert.Single(found, c => c.Path == GlobalExe);
        Assert.Equal(CnExe, found[0].Path); // 国服仍然排第一
    }

    [Fact]
    public void 手动解压的安装靠最近运行记录找回并按EXE名判断服务器()
    {
        var env = new FakeEnvironment();
        env.Recent.AddRange([
            @"D:\Game\GSCN\YuanShen.exe",
            @"D:\Game\Genshin Impact\GenshinImpact.exe",
            @"D:\Game\Missing\YuanShen.exe",
            @"D:\Game\Tools\Unlocker.exe"]);
        env.Files.Add(@"D:\Game\GSCN\YuanShen.exe");
        env.Files.Add(@"D:\Game\Genshin Impact\GenshinImpact.exe");
        env.Files.Add(@"D:\Game\Tools\Unlocker.exe");

        var found = GameExecutableFinder.Find(GameType.Genshin, env);

        Assert.Equal([@"D:\Game\GSCN\YuanShen.exe", @"D:\Game\Genshin Impact\GenshinImpact.exe"], found.Select(c => c.Path));
        Assert.Equal([0, 3], found.Select(c => c.Priority));
        Assert.All(found, c => Assert.Contains("最近运行", c.Source));
    }

    [Fact]
    public void 最近运行记录里的非盘符路径被忽略且同名游戏按目录名提示排序()
    {
        var env = new FakeEnvironment();
        env.Recent.AddRange([
            "/home/qian/work/ZenlessZoneZero.exe",
            @"D:\Game\Zenless Zone Zero\ZenlessZoneZero.exe",
            @"D:\Game\ZZZ_CN\ZenlessZoneZero.exe",
            @"D:\Game\ZZZ_Global\ZenlessZoneZero.exe"]);
        env.Files.Add("/home/qian/work/ZenlessZoneZero.exe");
        env.Files.Add(@"D:\Game\Zenless Zone Zero\ZenlessZoneZero.exe");
        env.Files.Add(@"D:\Game\ZZZ_CN\ZenlessZoneZero.exe");
        env.Files.Add(@"D:\Game\ZZZ_Global\ZenlessZoneZero.exe");

        var found = GameExecutableFinder.Find(GameType.ZenlessZoneZero, env);

        Assert.Equal(
            [@"D:\Game\ZZZ_CN\ZenlessZoneZero.exe", @"D:\Game\Zenless Zone Zero\ZenlessZoneZero.exe", @"D:\Game\ZZZ_Global\ZenlessZoneZero.exe"],
            found.Select(c => c.Path));
    }

    [Theory]
    [InlineData("nap_cn", 0)]
    [InlineData("hkrpg_cn", 0)]
    [InlineData("hk4e_bilibili", 1)]
    [InlineData("nap_global", 3)]
    [InlineData("崩坏：星穹铁道", 0)]
    [InlineData("Star Rail", 2)]
    public void 优先级按服务器标识推断(string name, int expected)
    {
        Assert.Equal(expected, GameExecutableFinder.PriorityOf(name));
    }

    [Fact]
    public void 真实系统实现在本机运行不抛异常()
    {
        // 只验证读取注册表 / 进程 / 磁盘的容错：不管本机装没装游戏，都应该正常返回。
        foreach (var game in Enum.GetValues<GameType>())
        {
            var found = GameExecutableFinder.Find(game, new SystemGameFinderEnvironment());
            Assert.All(found, c => Assert.True(GameExecutables.IsAllowed(game, c.Path)));
        }
    }
}
