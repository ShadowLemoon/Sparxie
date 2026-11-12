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

    /// <summary>
    /// 个别字段超范围或个别 Profile 无法修复：原文件已按原始字节备份（config.repaired-*.json），能修的字段改回默认值，
    /// 只有无法修复的 Profile 被移出，其余 Profile 原样保留。变更见 <see cref="AppConfigLoadResult.Notes"/>。
    /// </summary>
    Repaired,

    /// <summary>整份文件无法读取（不是 JSON、其他 schema）：原始字节已备份，并从最近一份可读的备份恢复了 Profile（见 RecoveredFrom），没有生成空白配置。</summary>
    RestoredFromBackup,

    /// <summary>目录不可写或写入失败，调用方应报错退出。</summary>
    Failed,
}

public sealed record AppConfigLoadResult(
    ConfigLoadState State,
    AppConfig Config,
    string? BackupPath,
    OperationError? Error,
    IReadOnlyList<string>? Notes = null,
    string? RecoveredFrom = null,
    string? RecoverableBackupPath = null)
{
    /// <summary>本次加载改了什么、移出了什么；调用方写入日志。</summary>
    public IReadOnlyList<string> Changes => Notes ?? [];
}
