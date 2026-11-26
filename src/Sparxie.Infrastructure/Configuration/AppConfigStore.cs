using System.Text.Json;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>
/// config.json 存储：无配置生成空白、逐个 Profile 读取与修复（见 docs/CONFIG-RECOVERY.md）、任何改动前先备份原始字节、原子保存。
/// 备份或新配置写入失败时保留原文件并以稳定错误码失败。
/// </summary>
public sealed class AppConfigStore
{
    private readonly string _configPath;

    public AppConfigStore(string configPath)
    {
        _configPath = configPath;
    }

    public string ConfigPath => _configPath;

    public AppConfigLoadResult Load()
    {
        if (!File.Exists(_configPath))
        {
            var empty = CreateEmptyConfig();
            try
            {
                Save(empty);
                return new AppConfigLoadResult(ConfigLoadState.CreatedNew, empty, null, null);
            }
            catch (IOException ex)
            {
                return Fail(ErrorCode.ConfigDirectoryNotWritable, "程序目录不可写，无法创建配置", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail(ErrorCode.ConfigDirectoryNotWritable, "程序目录不可写，无法创建配置", ex);
            }
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigReadFailed, "无法读取 config.json", ex);
        }

        var read = ConfigReader.Read(bytes);
        return read.WholeFileReadable ? LoadReadable(read, bytes) : LoadUnreadable(read.WholeFileError!, bytes);
    }

    private AppConfigLoadResult LoadReadable(ConfigReadResult read, byte[] originalBytes)
    {
        var config = read.Config!;
        if (!read.Changed)
        {
            return new AppConfigLoadResult(ConfigLoadState.Loaded, config, null, null);
        }

        // 有修复或移出：先按原始字节备份，成功后才写回。
        string backupPath;
        try
        {
            backupPath = CreateUniqueBackupPath("repaired");
            AtomicFile.WriteBytesNew(backupPath, originalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigBackupFailed, "无法备份需要修复的 config.json，保留原文件并退出", ex);
        }

        try
        {
            Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigWriteFailed, "备份成功但无法写回修复后的配置，保留原文件并退出", ex);
        }

        var changes = read.Quarantined.Concat(read.Repairs).Append($"原文件已备份为 {Path.GetFileName(backupPath)}").ToList();
        return new AppConfigLoadResult(ConfigLoadState.Repaired, config, backupPath, null, changes);
    }

    private AppConfigLoadResult LoadUnreadable(string why, byte[] bytes)
    {
        // 整份读不了：先备份原始字节，成功后才允许改动 config.json。
        string backupPath;
        try
        {
            backupPath = CreateUniqueBackupPath("invalid");
            AtomicFile.WriteBytesNew(backupPath, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigBackupFailed, "无法备份损坏的 config.json，保留原文件并退出", ex);
        }

        var recovered = FindRecoverable(excluding: backupPath);
        var config = recovered?.Config ?? CreateEmptyConfig();
        try
        {
            Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigWriteFailed, "备份成功但无法写入新配置，保留原文件并退出", ex);
        }

        var changes = new List<string> { $"config.json 无法读取（{why}），已备份为 {Path.GetFileName(backupPath)}" };
        if (recovered is not { } found)
        {
            changes.Add("没有可用的备份，生成了空白配置");
            return new AppConfigLoadResult(ConfigLoadState.RestoredFromCorrupt, config, backupPath, null, changes);
        }

        changes.Add($"已从 {Path.GetFileName(found.Path)} 恢复 {config.Profiles.Count} 个配置，这份备份可能不含最近的改动");
        changes.AddRange(found.Skipped);
        return new AppConfigLoadResult(ConfigLoadState.RestoredFromBackup, config, backupPath, null, changes);
    }

    /// <summary>config.backup-* 由外部工具在写入前留下；repaired / invalid 是本程序留下的原文件。</summary>
    private static readonly string[] RecoverablePatterns = ["config.backup-*.json", "config.repaired-*.json", "config.invalid-*.json"];

    /// <summary>config.json 旁边最新的、至少有一个可用 Profile 的备份；没有返回 null。</summary>
    private (string Path, AppConfig Config, IReadOnlyList<string> Skipped)? FindRecoverable(string excluding)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(_configPath))!);
            var candidates = RecoverablePatterns
                .SelectMany(pattern => directory.EnumerateFiles(pattern))
                .Where(file => !string.Equals(file.FullName, Path.GetFullPath(excluding), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal);

            foreach (var file in candidates)
            {
                try
                {
                    var read = ConfigReader.Read(File.ReadAllBytes(file.FullName));
                    if (read.WholeFileReadable && read.Config!.Profiles.Count > 0)
                    {
                        return (file.FullName, read.Config, read.Quarantined);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读不了的备份跳过
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录列不出来：当作没有备份
        }

        return null;
    }

    public void Save(AppConfig config)
    {
        var errors = AppConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("配置校验失败：" + string.Join("；", errors));
        }

        AtomicFile.WriteAllTextAtomic(_configPath, JsonSerializer.Serialize(config, ConfigJsonOptions.Serialize));
    }

    private static AppConfig CreateEmptyConfig() => new()
    {
        SchemaVersion = AppConfig.CurrentSchemaVersion,
        SelectedProfileId = null,
        Profiles = [],
    };

    private string CreateUniqueBackupPath(string kind)
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        var name = Path.GetFileNameWithoutExtension(_configPath);
        var utc = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        for (var i = 0; i < 32; i++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var candidate = Path.Combine(dir, $"{name}.{kind}-{utc}-{suffix}.json");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法生成唯一的配置备份文件名");
    }

    private static AppConfigLoadResult Fail(ErrorCode code, string message, Exception ex) =>
        new(ConfigLoadState.Failed, null!, null,
            new OperationError(StageCode.Validation, code, message, ex.HResult));
}
