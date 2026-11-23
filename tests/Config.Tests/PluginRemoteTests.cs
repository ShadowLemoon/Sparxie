using System.Text;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

public sealed class PluginRemoteTests
{
    private static string Manifest(string extra) =>
        $$"""{ "id": "ext.acme.demo", "displayName": "Demo", "abiMajor": 1, "exeNames": ["Demo.exe"], "caps": [] {{extra}} }""";

    private static bool Parse(string extra) =>
        PluginManifest.TryParse(Encoding.UTF8.GetBytes(Manifest(extra)), "ext.acme.demo", out _, out _);

    [Theory]
    [InlineData("https://cdn.example.com/bg.png", true)]
    [InlineData("http://cdn.example.com/bg.png", false)]
    [InlineData("https://localhost/bg.png", false)]
    [InlineData("https://127.0.0.1/bg.png", false)]
    [InlineData("https://192.168.1.5/bg.png", false)]
    [InlineData("https://10.0.0.1/bg.png", false)]
    [InlineData("https://169.254.169.254/latest", false)]
    [InlineData("https://user:pw@cdn.example.com/bg.png", false)]
    [InlineData("file:///c:/x.png", false)]
    [InlineData("", false)]
    public void 远程地址策略(string url, bool allowed) => Assert.Equal(allowed, RemoteUrlPolicy.IsAllowed(url));

    [Fact]
    public void 按路径从JSON取字符串_支持数组下标_类型不对或缺失返回false()
    {
        const string json = """{ "data": { "list": [ { "url": "https://a/1.png" }, { "url": "https://a/2.png" } ], "n": 3 } }""";

        Assert.True(RemoteUrlPolicy.TryReadString(json, "data.list.1.url", out var value));
        Assert.Equal("https://a/2.png", value);
        Assert.False(RemoteUrlPolicy.TryReadString(json, "data.n", out _));
        Assert.False(RemoteUrlPolicy.TryReadString(json, "data.list.5.url", out _));
        Assert.False(RemoteUrlPolicy.TryReadString("not json", "a", out _));
    }

    [Fact]
    public void 图片文件头校验()
    {
        Assert.True(RemoteUrlPolicy.LooksLikeImage([0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(RemoteUrlPolicy.LooksLikeImage([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(RemoteUrlPolicy.LooksLikeImage(Encoding.ASCII.GetBytes("<html><body>hello</body></html>")));
    }

    [Theory]
    [InlineData(""" , "backgroundUrl": "https://cdn.example.com/bg.png" """, true)]
    [InlineData(""" , "backgroundUrl": "http://cdn.example.com/bg.png" """, false)]
    [InlineData(""" , "backgroundApi": { "url": "https://api.example.com/bg", "path": "data.0.url" } """, true)]
    [InlineData(""" , "backgroundApi": { "url": "https://127.0.0.1/bg", "path": "a" } """, false)]
    [InlineData(""" , "backgroundApi": { "url": "https://api.example.com/bg", "path": "" } """, false)]
    [InlineData(""" , "autoFind": { "registry": [ { "hive": "HKCU", "key": "Software\\Demo", "value": "InstallPath" } ] } """, true)]
    [InlineData(""" , "autoFind": { "registry": [ { "hive": "HKCR", "key": "Software\\Demo", "value": "InstallPath" } ] } """, false)]
    [InlineData(""" , "autoFind": { "registry": [ { "hive": "HKLM", "key": "..\\Demo", "value": "InstallPath" } ] } """, false)]
    [InlineData(""" , "autoFind": { "registry": [ { "hive": "HKLM", "key": "Software\\Demo", "value": "P", "subdirectory": "..\\x" } ] } """, false)]
    public void Manifest的backgroundUrl_backgroundApi_registry校验(string extra, bool ok) => Assert.Equal(ok, Parse(extra));

    [Fact]
    public void 鹰角启动器的背景接口响应_按manifest声明取出图片和视频()
    {
        const string response = """
            {"proxy_rsps":[{"kind":"get_main_bg_image","get_main_bg_image_rsp":{"data_version":"","main_bg_image":{"url":"https://hg-utils-public.hycdn.cn/a.webp","md5":"x","video_url":"https://hg-utils-public.hycdn.cn/b.mp4"}}}]}
            """;
        var spec = new BackgroundApiSpec
        {
            Url = "https://launcher.hypergryph.com/api/proxy/web/batch_proxy",
            Path = "proxy_rsps.0.get_main_bg_image_rsp.main_bg_image",
            ItemPath = "url",
            VideoPath = "video_url",
        };

        var items = RemoteUrlPolicy.ReadBackgrounds(response, spec);

        Assert.Equal([("https://hg-utils-public.hycdn.cn/a.webp", (string?)"https://hg-utils-public.hycdn.cn/b.mp4")], items.ToArray());
    }

    [Fact]
    public void 背景接口返回数组_每项一张_非法地址和超出数量被丢弃()
    {
        var urls = string.Join(",", Enumerable.Range(0, 12).Select(i => $$"""{ "u": "https://cdn.example.com/{{i}}.png" }""")) + """, { "u": "http://bad/x.png" }""";
        var items = RemoteUrlPolicy.ReadBackgrounds($$"""{ "list": [ {{urls}} ] }""", new BackgroundApiSpec { Path = "list", ItemPath = "u" });

        Assert.Equal(RemoteUrlPolicy.MaxBackgrounds, items.Count);
        Assert.All(items, item => Assert.StartsWith("https://", item.Image));
        Assert.Empty(RemoteUrlPolicy.ReadBackgrounds("not json", new BackgroundApiSpec { Path = "a" }));
    }

    [Theory]
    [InlineData(""" , "backgroundApi": { "url": "https://l.example.com/x", "method": "POST", "body": { "seq": "5" }, "path": "a", "itemPath": "u" } """, true)]
    [InlineData(""" , "backgroundApi": { "url": "https://l.example.com/x", "method": "DELETE", "path": "a" } """, false)]
    [InlineData(""" , "backgroundApi": { "url": "https://l.example.com/x", "method": "POST", "body": "text", "path": "a" } """, false)]
    public void backgroundApi的POST请求体必须是JSON对象(string extra, bool ok) => Assert.Equal(ok, Parse(extra));

    private sealed class Env : IGameFinderEnvironment
    {
        public Dictionary<string, string> Registry { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories() => [];
        public IEnumerable<(string Name, string Directory)> UninstallInstallDirectories() => [];
        public IEnumerable<string> RecentExecutablePaths() => [];
        public IEnumerable<string> RunningExecutablePaths(string executableName) => [];
        public bool FileExists(string path) => Files.Contains(path);
        public IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth) => [];
        public IEnumerable<string> RegistryDirectories(RegistryLookupSpec lookup) =>
            Registry.TryGetValue($"{lookup.Hive}\\{lookup.Key}\\{lookup.Value}", out var value) ? [value] : [];
    }

    [Fact]
    public void 插件传入的注册表查找项_目录_子目录_EXE路径都能用()
    {
        var game = new GameDescriptor("ext.acme.demo", "Demo", ["Demo.exe"], RegistryLookups:
        [
            new RegistryLookupSpec { Hive = "HKCU", Key = @"Software\Demo", Value = "InstallPath", Subdirectory = "game" },
            new RegistryLookupSpec { Hive = "HKLM", Key = @"Software\Demo2", Value = "Exe" },
        ]);
        var env = new Env();
        env.Registry[@"HKCU\Software\Demo\InstallPath"] = "E:/Demo";
        env.Registry[@"HKLM\Software\Demo2\Exe"] = "\"F:\\Other\\Demo.exe\"";
        env.Files.Add(@"E:\Demo\game\Demo.exe");
        env.Files.Add(@"F:\Other\Demo.exe");

        var found = GameExecutableFinder.FindThirdParty(game, env);

        Assert.Equal([@"E:\Demo\game\Demo.exe", @"F:\Other\Demo.exe"], found.Select(item => item.Path).Order().ToArray());
    }
}
