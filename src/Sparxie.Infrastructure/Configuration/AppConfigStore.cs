using System.Text.Json;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>
/// config.json 存储：无配置生成空白、损坏先按原始字节备份再生成空白、原子保存。
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
        if (read.WholeFileReadable)
        {
            return LoadReadable(read, bytes);
        }

        return LoadUnreadable(read.WholeFileError ?? "无法读取", bytes);
    }

    private AppConfigLoadResult LoadReadable(ConfigReadResult read, byte[] originalBytes)
    {
        var config = read.Config!;
        if (!read.Changed)
        {
            if (read.Migrated)
            {
                // the one-time migration of the removed Endfield input 自动 is kept: the next start reads the new value
                try
                {
                    Save(config);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // read-only folder: the value is migrated in memory on every load
                }
            }

            // a config without profiles beside a backup that has some: say so, never restore on its own (the person may have removed them)
            var recoverable = config.Profiles.Count == 0 ? FindRecoverable(excluding: null) : null;
            var notes = recoverable is null ? null : new[] { $"当前配置没有 Profile，但 {Path.GetFileName(recoverable.Value.Path)} 里有 {recoverable.Value.ProfileCount} 个可读的 Profile（未自动恢复）" };
            return new AppConfigLoadResult(ConfigLoadState.Loaded, config, null, null, notes, null, recoverable?.Path);
        }

        // 有修复或移出：先按原始字节备份，成功后才写回；备份或写回失败则保留原文件并失败退出
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail(ErrorCode.ConfigWriteFailed, "备份成功但无法写回修复后的配置，保留原文件并退出", ex);
        }

        var changes = read.Repairs.Concat(read.Quarantined).Append($"原文件已备份为 {Path.GetFileName(backupPath)}").ToList();
        return new AppConfigLoadResult(ConfigLoadState.Repaired, config, backupPath, null, changes);
    }

    private AppConfigLoadResult LoadUnreadable(string why, byte[] bytes)
    {
        // 整份文件无法读取：先备份原始字节，成功后才允许改动 config.json。
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

        // 不静默丢掉 Profile：有可读的备份（写入前留下的 config.backup-*、修复前的 config.repaired-*、以前移走的 config.invalid-*）就恢复最近一份，
        // 没有才生成空白配置。恢复的来源写进 Notes / RecoveredFrom，原文件仍在 backupPath。
        var recoverable = FindRecoverable(excluding: backupPath);
        if (recoverable is { } found)
        {
            var recovered = ConfigReader.Read(File.ReadAllBytes(found.Path)).Config!;
            try
            {
                Save(recovered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Fail(ErrorCode.ConfigWriteFailed, "备份成功但无法写回恢复的配置，保留原文件并退出", ex);
            }

            return new AppConfigLoadResult(ConfigLoadState.RestoredFromBackup, recovered, backupPath, null,
                [$"config.json 无法读取（{why}），已备份为 {Path.GetFileName(backupPath)}", $"已从 {Path.GetFileName(found.Path)} 恢复 {recovered.Profiles.Count} 个 Profile"], found.Path);
        }

        var fresh = CreateEmptyConfig();
        try
        {
            Save(fresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigWriteFailed, "备份成功但无法生成新配置，保留原文件并退出", ex);
        }

        return new AppConfigLoadResult(ConfigLoadState.RestoredFromCorrupt, fresh, backupPath, null,
            [$"config.json 无法读取（{why}），已备份为 {Path.GetFileName(backupPath)}，没有可恢复的备份，生成了空白配置"]);
    }

    /// <summary>The newest file beside config.json that holds at least one readable profile, newest first; null when there is none.</summary>
    private (string Path, int ProfileCount)? FindRecoverable(string? excluding)
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var candidates = new List<FileInfo>();
        foreach (var pattern in RecoverablePatterns)
        {
            candidates.AddRange(new DirectoryInfo(dir).EnumerateFiles(pattern));
        }

        foreach (var file in candidates.OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal))
        {
            if (excluding is not null && string.Equals(file.FullName, excluding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var read = ConfigReader.Read(File.ReadAllBytes(file.FullName));
                if (read.WholeFileReadable && read.Config!.Profiles.Count > 0)
                {
                    return (file.FullName, read.Config.Profiles.Count);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an unreadable candidate is skipped
            }
        }

        return null;
    }

    private static readonly string[] RecoverablePatterns = ["config.backup-*.json", "config.repaired-*.json", "config.invalid-*.json"];

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

    /// <summary>
    /// Endfield's input used to have a third choice, 自动 ("auto": by the client's device). It is gone: a stored "auto" becomes "touch" (a Sparxie session is a touch
    /// session unless the person chose keyboard, mouse and gamepad). True when something changed.
    /// </summary>
    public static bool MigrateEndfieldInput(AppConfig config)
    {
        var changed = false;
        foreach (var profile in config.Profiles)
        {
            changed |= MigrateEndfieldInput(profile);
        }

        return changed;
    }

    public static bool MigrateEndfieldInput(GameProfile profile)
    {
        if (profile.Game == GameType.Endfield && profile.Hoyo is { } hoyo && hoyo.EndfieldInputMode is not ("touch" or "pc"))
        {
            hoyo.EndfieldInputMode = "touch";
            return true;
        }

        return false;
    }

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
