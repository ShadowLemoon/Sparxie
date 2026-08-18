using Sparxie.Infrastructure.Zzz;
using Xunit;

namespace Zzz.Tests;

public class ZzzRuntimeManifestTests
{
    [Fact]
    public void 合法清单通过校验()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zzz-runtime.json");
        File.WriteAllText(path, """
        {
          "runtimeVersion": "v1.0.0",
          "releaseAsset": "ZZZTouchRuntime-v1.0.0-win-x64.zip",
          "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "files": ["ZZZTouchCore.dll", "ZZZTouchRuntime.dll"],
          "exeWhiteList": ["ZenlessZoneZero.exe", "ZenlessZoneZeroBeta.exe"]
        }
        """);

        var manifest = ZzzRuntimeManifest.Load(path);
        Assert.Equal("v1.0.0", manifest.RuntimeVersion);
        Assert.Equal(2, manifest.Files.Count);
    }

    [Fact]
    public void 仓库固定Runtime清单与v100发布资产一致()
    {
        var manifest = ZzzRuntimeManifest.Load(Path.Combine(RepoRoot(), "build", "zzz-runtime.json"));

        Assert.Equal("v1.0.0", manifest.RuntimeVersion);
        Assert.Equal("ZZZTouchRuntime-v1.0.0-win-x64.zip", manifest.ReleaseAsset);
        Assert.Equal("e8fba7e8b237ecd9806a225bdaced97cc0f1eb8ae22b344ae0d3f47439e4f1c2", manifest.Sha256);
        Assert.Equal(["ZZZTouchCore.dll", "ZZZTouchRuntime.dll"], manifest.Files);
        Assert.Equal(["ZenlessZoneZero.exe", "ZenlessZoneZeroBeta.exe"], manifest.ExeWhiteList);
    }

    [Fact]
    public void 空版本被拒绝()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zzz-runtime.json");
        File.WriteAllText(path, """
        {
          "runtimeVersion": "",
          "releaseAsset": "x.zip",
          "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "files": ["ZZZTouchCore.dll"],
          "exeWhiteList": ["ZenlessZoneZero.exe"]
        }
        """);

        Assert.Throws<InvalidDataException>(() => ZzzRuntimeManifest.Load(path));
    }

    [Fact]
    public void 非64位哈希被拒绝()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zzz-runtime.json");
        File.WriteAllText(path, """
        {
          "runtimeVersion": "v1",
          "releaseAsset": "x.zip",
          "sha256": "abcd",
          "files": ["ZZZTouchCore.dll"],
          "exeWhiteList": ["ZenlessZoneZero.exe"]
        }
        """);

        Assert.Throws<InvalidDataException>(() => ZzzRuntimeManifest.Load(path));
    }

    [Fact]
    public void 路径型文件名被拒绝()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zzz-runtime.json");
        File.WriteAllText(path, """
        {
          "runtimeVersion": "v1",
          "releaseAsset": "x.zip",
          "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "files": ["..\\evil.dll"],
          "exeWhiteList": ["ZenlessZoneZero.exe"]
        }
        """);

        Assert.Throws<InvalidDataException>(() => ZzzRuntimeManifest.Load(path));
    }

    [Fact]
    public void 缺少Core或包含重复文件名被拒绝()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zzz-runtime.json");
        File.WriteAllText(path, """
        {
          "runtimeVersion": "v1",
          "releaseAsset": "x.zip",
          "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "files": ["ZZZTouchRuntime.dll", "ZZZTouchRuntime.dll"],
          "exeWhiteList": ["ZenlessZoneZero.exe"]
        }
        """);

        Assert.Throws<InvalidDataException>(() => ZzzRuntimeManifest.Load(path));
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
