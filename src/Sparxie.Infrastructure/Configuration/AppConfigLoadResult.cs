using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Configuration;

public enum ConfigLoadState
{
    /// <summary>正常加载既有配置。</summary>
    Loaded,

    /// <summary>无配置时生成空白当前 schema 配置。</summary>
    CreatedNew,

    /// <summary>配置损坏：已备份原始字节并生成空白配置。</summary>
    RestoredFromCorrupt,

    /// <summary>目录不可写或写入失败，调用方应报错退出。</summary>
    Failed,
}

public sealed record AppConfigLoadResult(
    ConfigLoadState State,
    AppConfig Config,
    string? BackupPath,
    OperationError? Error);
