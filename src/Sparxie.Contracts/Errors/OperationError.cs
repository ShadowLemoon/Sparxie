namespace Sparxie.Contracts.Errors;

/// <summary>带阶段与稳定错误码的操作结果。Win32/HRESULT/native 子错误只作诊断详情。</summary>
public sealed record OperationError(StageCode Stage, ErrorCode Code, string Message, int? NativeError = null)
{
    public static OperationError Ok(StageCode stage) => new(stage, ErrorCode.None, string.Empty);
}
