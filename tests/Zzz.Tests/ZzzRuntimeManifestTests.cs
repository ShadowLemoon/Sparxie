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
          "runtimeVersion": "2026.08.1",
          "releaseAsset": "zzz-runtime-2026.08.1.zip",
          "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "files": ["ZZZTouchCore.dll", "ZZZTouchFilterHook.dll"],
          "exeWhiteList": ["ZenlessZoneZero.exe", "ZenlessZoneZeroBeta.exe"]
        }
        """);

        var manifest = ZzzRuntimeManifest.Load(path);
        Assert.Equal("2026.08.1", manifest.RuntimeVersion);
        Assert.Equal(2, manifest.Files.Count);
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
}
