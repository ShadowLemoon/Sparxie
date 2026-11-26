using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Configuration;

public enum ConfigLoadState
{
    /// <summary>正常加载既有配置。</summary>
    Loaded,

    /// <summary>无配置时生成空白当前 schema 配置。</summary>
    CreatedNew,

    /// <summary>整份文件读不了且没有可用备份：已备份原始字节并生成空白配置。</summary>
    RestoredFromCorrupt,

    /// <summary>个别字段或个别 Profile 有问题：原文件已备份（config.repaired-*.json），能修的修好，修不了的 Profile 移出，其余保留。</summary>
    Repaired,

    /// <summary>整份文件读不了：原始字节已备份，并用旁边最近一份可读的备份恢复。</summary>
    RestoredFromBackup,

    /// <summary>目录不可写或写入失败，调用方应报错退出。</summary>
    Failed,
}

public sealed record AppConfigLoadResult(
    ConfigLoadState State,
    AppConfig Config,
    string? BackupPath,
    OperationError? Error,
    IReadOnlyList<string>? Notes = null)
{
    /// <summary>本次加载修了什么、移出了什么、从哪里恢复；调用方写进日志并告诉用户。</summary>
    public IReadOnlyList<string> Changes => Notes ?? [];
}
