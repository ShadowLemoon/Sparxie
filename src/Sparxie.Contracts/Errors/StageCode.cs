namespace Sparxie.Contracts.Errors;

/// <summary>启动/会话生命周期阶段，用于稳定阶段码。</summary>
public enum StageCode
{
    Validation,
    Mutex,
    RecoveryRecord,
    Job,
    CreateProcess,
    TouchScan,
    FpsScan,
    Patch,
    Inject,
    Restore,
    Running,
    Wait,
    HostFault,
    Cleanup,
}
