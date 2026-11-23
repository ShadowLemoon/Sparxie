using System.Text;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

public sealed class PluginAssetsAndAutoFindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-assets-" + Guid.NewGuid().ToString("N"));

    public PluginAssetsAndAutoFindTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Manifest(string extra) =>
        $$"""{ "id": "ext.acme.demo", "displayName": "Demo", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [] {{extra}} }""";

    private static bool Parse(string json, out PluginManifest manifest, out string error) =>
        PluginManifest.TryParse(Encoding.UTF8.GetBytes(json), "ext.acme.demo", out manifest, out error);

    [Theory]
    [InlineData(""" , "icon": "icon.png", "background": "bg.jpg" """, true)]
    [InlineData(""" , "icon": "..\\icon.png" """, false)]
    [InlineData(""" , "background": "sub/bg.png" """, false)]
    [InlineData(""" , "icon": "icon.exe" """, false)]
    [InlineData(""" , "autoFind": { "uninstallNames": ["Demo"] } """, true)]
    [InlineData(""" , "autoFind": { "uninstallNames": ["x"] } """, false)]
    public void Manifest的icon_background_autoFind校验(string extra, bool ok)
    {
        Assert.Equal(ok, Parse(Manifest(extra), out _, out _));
    }

    [Fact]
    public void 资源只在文件存在且大小合理时才返回路径()
    {
        File.WriteAllBytes(Path.Combine(_dir, "icon.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "empty.png"), []);

        Assert.Equal(Path.Combine(_dir, "icon.png"), PluginAssets.Resolve(_dir, "icon.png"));
        Assert.Null(PluginAssets.Resolve(_dir, "empty.png"));
        Assert.Null(PluginAssets.Resolve(_dir, "missing.png"));
        Assert.Null(PluginAssets.Resolve(_dir, "..\\icon.png"));
        Assert.Null(PluginAssets.Resolve(_dir, null));
    }

    private sealed class Env : IGameFinderEnvironment
    {
        public List<(string Name, string Directory)> Uninstall { get; } = [];
        public List<string> Recent { get; } = [];
        public List<string> Running { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories() => [];
        public IEnumerable<(string Name, string Directory)> UninstallInstallDirectories() => Uninstall;
        public IEnumerable<string> RecentExecutablePaths() => Recent;
        public IEnumerable<string> RunningExecutablePaths(string executableName) => Running;
        public bool FileExists(string path) => Files.Contains(path);
        public IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth) =>
            Files.Where(f => f.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 第三方游戏自动查找_卸载项提示优先_只收白名单文件名_排除非盘符路径()
    {
        var game = new GameDescriptor("ext.acme.demo", "Demo", ["Demo.exe"], UninstallNames: ["DemoLauncher"]);
        var env = new Env();
        env.Uninstall.Add(("DemoLauncher_x", @"E:\Demo"));
        env.Uninstall.Add(("OtherThing", @"F:\Other"));
        foreach (var file in new[] { @"E:\Demo\game\Demo.exe", @"F:\Other\Demo.exe", @"D:\Run\Demo.exe", @"D:\Run\Other.exe", "/home/x/Demo.exe" })
        {
            env.Files.Add(file);
        }

        env.Recent.AddRange([@"D:\Run\Demo.exe", @"D:\Run\Other.exe", "/home/x/Demo.exe"]);
        env.Running.Add(@"D:\Run\Demo.exe");

        var found = GameExecutableFinder.FindThirdParty(game, env);

        Assert.Equal([@"E:\Demo\game\Demo.exe", @"D:\Run\Demo.exe"], found.Select(item => item.Path).ToArray());
    }
}
