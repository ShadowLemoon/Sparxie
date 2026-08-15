namespace Sparxie.Contracts.Errors;

/// <summary>稳定错误码：跨进程、跨版本可读，不暴露 native 指针、句柄或 C++ 异常。</summary>
public enum ErrorCode
{
    None = 0,

    // 通用
    InvalidArgument,
    Unauthorized,
    ProtocolVersionMismatch,

    // 配置
    ConfigDirectoryNotWritable,
    ConfigReadFailed,
    ConfigWriteFailed,
    ConfigInvalid,
    ConfigBackupFailed,

    // 启动与会话
    GameAlreadyRunning,
    MutexConflict,
    ProfileNotFound,
    SessionNotFound,
    InvalidExecutableName,
    ExecutableNotFound,
    JobSetupFailed,
    ProcessCreateFailed,

    // 能力校验与注入
    TouchScanFailed,
    FpsScanFailed,
    PatchFailed,
    InjectFailed,
    InstallConfirmFailed,

    // ZZZ 恢复
    RecoveryRecordPresent,
    RecoveryAssetCorrupt,
    RecoveryPathInvalid,
    RecoveryGameStillRunning,
    RecoveryRestoreFailed,

    // Host/运行期
    HostCrashedBeforeRunning,
    HostCrashedAfterRunning,
    RuntimeLoadFailed,
}
