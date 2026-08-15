using Sparxie.Infrastructure.Zzz;
using Xunit;

namespace Zzz.Tests;

[Collection("ZzzStorage")]
public class ZzzGeneralDataTests
{
    private static string CreateSampleFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-zzz-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "GENERAL_DATA.bin");
        ZzzGeneralData.WriteRawString(path, content);
        return path;
    }

    [Fact]
    public void 写入后能读回相同内容()
    {
        const string content = "{\"LocalUILayoutPlatform\": 2, \"Other\": 1}";
        var path = CreateSampleFile(content);

        Assert.Equal(2, ZzzGeneralData.ReadPlatform(path));
    }

    [Fact]
    public void 平台切换触屏与PC往返()
    {
        var path = CreateSampleFile("{\"LocalUILayoutPlatform\": 2}");

        ZzzGeneralData.WritePlatform(path, ZzzGeneralData.PlatformTouch);
        Assert.Equal(ZzzGeneralData.PlatformTouch, ZzzGeneralData.ReadPlatform(path));

        ZzzGeneralData.WritePlatform(path, ZzzGeneralData.PlatformPc);
        Assert.Equal(ZzzGeneralData.PlatformPc, ZzzGeneralData.ReadPlatform(path));
    }

    [Fact]
    public void 缺失字段按PC处理()
    {
        var path = CreateSampleFile("{\"Other\": 123}");
        Assert.Equal(ZzzGeneralData.PlatformPc, ZzzGeneralData.ReadPlatform(path));
    }

    [Fact]
    public void 损坏头部文件抛异常()
    {
        var path = CreateSampleFile("{\"LocalUILayoutPlatform\": 2}");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.ThrowsAny<Exception>(() => ZzzGeneralData.ReadPlatform(path));
    }
}
