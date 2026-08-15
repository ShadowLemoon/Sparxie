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

        if (TryParse(bytes, out var config, out _))
        {
            return new AppConfigLoadResult(ConfigLoadState.Loaded, config, null, null);
        }

        // 损坏：先备份原始字节，成功后才允许生成空白配置。
        string backupPath;
        try
        {
            backupPath = CreateUniqueBackupPath();
            AtomicFile.WriteBytesNew(backupPath, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.ConfigBackupFailed, "无法备份损坏的 config.json，保留原文件并退出", ex);
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

        return new AppConfigLoadResult(ConfigLoadState.RestoredFromCorrupt, fresh, backupPath, null);
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

    private bool TryParse(byte[] bytes, out AppConfig config, out string error)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<AppConfig>(bytes, ConfigJsonOptions.Read);
            if (parsed is null)
            {
                error = "config.json 为空或 null";
                config = null!;
                return false;
            }

            var validationErrors = AppConfigValidator.Validate(parsed);
            if (validationErrors.Count > 0)
            {
                error = string.Join("；", validationErrors);
                config = null!;
                return false;
            }

            config = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            config = null!;
            return false;
        }
    }

    private string CreateUniqueBackupPath()
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        var name = Path.GetFileNameWithoutExtension(_configPath);
        var utc = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        for (var i = 0; i < 32; i++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var candidate = Path.Combine(dir, $"{name}.invalid-{utc}-{suffix}.json");
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
