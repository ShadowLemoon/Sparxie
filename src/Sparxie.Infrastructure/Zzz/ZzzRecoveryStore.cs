using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sparxie.Infrastructure.Zzz;

/// <summary>
/// 绝区零会话恢复记录：修改 GENERAL_DATA.bin 前把原始 PC 配置按字节备份到
/// recovery/zzz/{sessionId}/，并写入版本化 manifest。恢复例程为正常清理与
/// 异常接管共享：校验 manifest、允许的路径和备份完整性，原子恢复并刷盘，
/// 成功后删除恢复资产。只有 Broker 同时不可用、进程退出无法确认或恢复失败时，
/// 恢复资产才保留给下次启动兜底。
/// </summary>
public static class ZzzRecoveryStore
{
    public const string ManifestSchemaVersion = "1";

    public static string RecoveryRelativeRoot =>
        Path.Combine("recovery", "zzz");

    /// <summary>应用根目录：默认取当前进程 BaseDirectory，可由 SPARXIE_APP_DIR 覆盖（Broker 传给 SessionHost 时保证同目录）。</summary>
    public static string AppRoot =>
        Environment.GetEnvironmentVariable("SPARXIE_APP_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : AppContext.BaseDirectory;

    public static string RootPath =>
        Path.Combine(AppRoot, RecoveryRelativeRoot);

    public static string SessionPath(string sessionId) =>
        Path.Combine(RootPath, sessionId);

    private static string ManifestPath(string sessionId) =>
        Path.Combine(SessionPath(sessionId), "manifest.json");

    private static string BackupPath(string sessionId) =>
        Path.Combine(SessionPath(sessionId), "GENERAL_DATA.bin.bak");

    /// <summary>创建恢复会话：备份原始字节并写 manifest，全部落盘后才算成功。</summary>
    public static ZzzRecoveryRecord Create(string sessionId, string generalDataPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(generalDataPath) || !File.Exists(generalDataPath))
        {
            throw new FileNotFoundException("GENERAL_DATA.bin 不存在", generalDataPath);
        }

        ValidateAllowedPath(generalDataPath);

        var sessionDir = SessionPath(sessionId);
        if (Directory.Exists(sessionDir) && File.Exists(ManifestPath(sessionId)))
        {
            throw new InvalidOperationException($"会话 {sessionId} 已存在恢复记录，必须先处理");
        }

        Directory.CreateDirectory(sessionDir);

        var backupBytes = File.ReadAllBytes(generalDataPath);
        var record = new ZzzRecoveryRecord
        {
            SchemaVersion = ManifestSchemaVersion,
            SessionId = sessionId,
            GeneralDataPath = Path.GetFullPath(generalDataPath),
            BackupSha256 = Convert.ToHexString(SHA256.HashData(backupBytes)),
            CreatedUtc = DateTime.UtcNow,
        };

        // 先写备份，再写 manifest：manifest 存在即代表备份可用。
        File.WriteAllBytes(BackupPath(sessionId), backupBytes);
        WriteManifest(record);

        return record;
    }

    /// <summary>读取指定会话的恢复记录；不存在返回 null。</summary>
    public static ZzzRecoveryRecord? TryLoad(string sessionId)
    {
        var path = ManifestPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        return ReadManifest(path);
    }

    /// <summary>枚举全部遗留恢复记录（Broker 启动或下次 ZZZ 启动前的双重故障兜底）。</summary>
    public static IReadOnlyList<ZzzRecoveryRecord> FindAll()
    {
        if (!Directory.Exists(RootPath))
        {
            return [];
        }

        var records = new List<ZzzRecoveryRecord>();
        foreach (var dir in Directory.GetDirectories(RootPath))
        {
            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                records.Add(ReadManifest(manifest));
            }
            catch
            {
                // 损坏 manifest：保留目录，由恢复路径报告并阻止新 ZZZ 启动
            }
        }

        return records;
    }

    /// <summary>
    /// 共享恢复例程：校验 manifest、允许的 ZZZ 配置路径与备份完整性，
    /// 确认游戏进程已退出，原子恢复并刷盘 GENERAL_DATA.bin，成功后删除恢复资产。
    /// 任一校验失败都保留恢复资产并抛出异常。
    /// </summary>
    public static void Restore(ZzzRecoveryRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        if (record.SchemaVersion != ManifestSchemaVersion)
        {
            throw new InvalidDataException($"恢复记录 schema 版本不受支持: {record.SchemaVersion}");
        }

        var backup = BackupPath(record.SessionId);
        if (!File.Exists(backup))
        {
            throw new FileNotFoundException("恢复备份缺失", backup);
        }

        // 备份完整性：与 manifest 记录一致
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup)));
        if (!string.Equals(actualHash, record.BackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("恢复备份与 manifest 哈希不一致，拒绝写回");
        }

        var target = record.GeneralDataPath;
        ValidateAllowedPath(target);

        // 游戏仍运行时拒绝自动恢复（对应进程已退出才算安全）
        if (IsGameRunningForPath(target))
        {
            throw new InvalidOperationException("对应游戏进程仍在运行，拒绝自动恢复");
        }

        // 原子恢复：先复制到目标同目录临时文件，再 File.Replace 覆盖并刷盘。
        var targetDir = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("GENERAL_DATA.bin 目标目录无效");
        var tempPath = Path.Combine(targetDir, $".GENERAL_DATA.bin.{record.SessionId}.tmp");
        try
        {
            File.Copy(backup, tempPath, overwrite: true);
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Flush(flushToDisk: true);
            }

            File.Replace(tempPath, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 临时文件清理失败不掩盖恢复结果
            }
        }

        // 恢复成功后删除恢复资产
        Delete(record);
    }

    /// <summary>删除指定会话的恢复资产；仅在成功恢复或明确处置后调用。</summary>
    public static void Delete(ZzzRecoveryRecord record)
    {
        var dir = SessionPath(record.SessionId);
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"恢复资产删除失败: {dir}", ex);
            }
        }
    }

    /// <summary>是否存在任何遗留恢复记录。</summary>
    public static bool HasPending() => FindAll().Count > 0;

    private static void ValidateAllowedPath(string path)
    {
        var full = Path.GetFullPath(path);
        var fileName = Path.GetFileName(full);
        if (!string.Equals(fileName, "GENERAL_DATA.bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"不允许的恢复目标: {full}");
        }

        // 必须形如 {gameRoot}\*_Data\Persistent\LocalStorage\GENERAL_DATA.bin
        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length < 4)
        {
            throw new InvalidDataException($"不允许的恢复目标: {full}");
        }

        var dataDir = segments[^4];
        var persistent = segments[^3];
        var localStorage = segments[^2];
        if (!dataDir.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(persistent, "Persistent", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(localStorage, "LocalStorage", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"不允许的恢复目标: {full}");
        }
    }

    private static bool IsGameRunningForPath(string generalDataPath)
    {
        var segments = generalDataPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length < 4)
        {
            return false;
        }

        // 对应游戏根目录为 *_Data 的上两级（ZenlessZoneZero 安装目录）
        var gameRoot = string.Join(Path.DirectorySeparatorChar.ToString(), segments[..^4]);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return false;
        }

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var exePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    continue;
                }

                var processDir = Path.GetDirectoryName(exePath) ?? string.Empty;
                if (string.Equals(processDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 进程已退出或无权访问
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static void WriteManifest(ZzzRecoveryRecord record)
    {
        var json = JsonSerializer.Serialize(record, ManifestOptions);
        var temp = ManifestPath(record.SessionId) + ".tmp";
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using (var fs = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Flush(flushToDisk: true);
            }

            File.Move(temp, ManifestPath(record.SessionId), overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 清理失败不掩盖写入结果
            }
        }
    }

    private static ZzzRecoveryRecord ReadManifest(string manifestPath)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ZzzRecoveryRecord>(File.ReadAllText(manifestPath), ManifestOptions);
            if (record is null || string.IsNullOrWhiteSpace(record.SessionId))
            {
                throw new InvalidDataException("manifest 内容无效");
            }

            return record;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"manifest 解析失败: {manifestPath}", ex);
        }
    }

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>恢复记录 manifest 模型（schema v1）。</summary>
public sealed class ZzzRecoveryRecord
{
    public string SchemaVersion { get; set; } = ZzzRecoveryStore.ManifestSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string GeneralDataPath { get; set; } = string.Empty;

    public string BackupSha256 { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}
