using System.Security.Cryptography;
using Sparxie.Infrastructure.Zzz;
using Xunit;

namespace Zzz.Tests;

[Collection("ZzzStorage")]
public class ZzzRecoveryStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _generalDataPath;
    private readonly List<string> _sessions = [];

    public ZzzRecoveryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sparxie-zzz-recovery-tests", Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "ZenlessZoneZero_Data", "Persistent", "LocalStorage");
        Directory.CreateDirectory(_dataDir);
        _generalDataPath = Path.Combine(_dataDir, "GENERAL_DATA.bin");
        ZzzGeneralData.WriteRawString(_generalDataPath, "{\"LocalUILayoutPlatform\": 2}");
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            try
            {
                var dir = Path.Combine(ZzzRecoveryStore.RootPath, session);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private ZzzRecoveryRecord CreateRecord(string sessionId)
    {
        _sessions.Add(sessionId);
        return ZzzRecoveryStore.Create(sessionId, _generalDataPath);
    }

    [Fact]
    public void 创建恢复记录后备份与manifest存在()
    {
        var record = CreateRecord("s1");
        Assert.NotNull(record);
        Assert.Equal("1", record.SchemaVersion);
        Assert.Equal(Path.GetFullPath(_generalDataPath), record.GeneralDataPath);

        var sessionDir = Path.Combine(ZzzRecoveryStore.RootPath, "s1");
        Assert.True(File.Exists(Path.Combine(sessionDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(sessionDir, "GENERAL_DATA.bin.bak")));
    }

    [Fact]
    public void 恢复例程还原原始字节并删除恢复资产()
    {
        var originalBytes = File.ReadAllBytes(_generalDataPath);
        var record = CreateRecord("s2");

        // 修改游戏配置（模拟写入触屏配置）
        ZzzGeneralData.WritePlatform(_generalDataPath, ZzzGeneralData.PlatformTouch);
        Assert.Equal(ZzzGeneralData.PlatformTouch, ZzzGeneralData.ReadPlatform(_generalDataPath));

        ZzzRecoveryStore.Restore(record);

        Assert.Equal(originalBytes, File.ReadAllBytes(_generalDataPath));
        Assert.Null(ZzzRecoveryStore.TryLoad("s2"));
        Assert.False(Directory.Exists(Path.Combine(ZzzRecoveryStore.RootPath, "s2")));
    }

    [Fact]
    public void 备份被篡改时拒绝恢复且保留资产()
    {
        var record = CreateRecord("s3");

        // 篡改备份
        var backup = Path.Combine(ZzzRecoveryStore.RootPath, "s3", "GENERAL_DATA.bin.bak");
        File.WriteAllBytes(backup, [9, 9, 9, 9]);

        Assert.Throws<InvalidDataException>(() => ZzzRecoveryStore.Restore(record));
        Assert.NotNull(ZzzRecoveryStore.TryLoad("s3"));
    }

    [Fact]
    public void 目标路径越界时拒绝创建()
    {
        var outside = Path.Combine(_root, "evil", "GENERAL_DATA.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => ZzzRecoveryStore.Create("s4", outside));
    }

    [Fact]
    public void 同会话重复创建被拒绝()
    {
        CreateRecord("s5");
        Assert.Throws<InvalidOperationException>(() => ZzzRecoveryStore.Create("s5", _generalDataPath));
    }

    [Fact]
    public void 恢复后遗留记录被清除()
    {
        // 清理其他测试可能遗留的记录，保证全局断言独立
        foreach (var pending in ZzzRecoveryStore.FindAll())
        {
            ZzzRecoveryStore.Delete(pending);
        }

        var record = CreateRecord("s6");
        Assert.True(ZzzRecoveryStore.HasPending());

        ZzzRecoveryStore.Restore(record);

        Assert.False(ZzzRecoveryStore.HasPending());
    }
}
