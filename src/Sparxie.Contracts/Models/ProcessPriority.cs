namespace Sparxie.Contracts.Models;

/// <summary>
/// 进程优先级公共语义（四档）。
/// Realtime 不进入 UI、配置公共枚举和 RPC；Hoyo 适配层再映射到 Win32 与上游值。
/// </summary>
public enum ProcessPriority
{
    Normal = 0,
    BelowNormal = 1,
    AboveNormal = 2,
    High = 3,
}
