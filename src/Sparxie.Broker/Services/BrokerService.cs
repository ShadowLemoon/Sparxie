using Grpc.Core;
using Microsoft.Extensions.Logging;
using Sparxie.Broker.Validation;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Rpc;

namespace Sparxie.Broker.Services;

/// <summary>
/// UI 只连接 Broker。本服务负责协议/请求校验与转发；
/// SessionHost 私有管道与转发链在 SessionHost 步骤接入。
/// </summary>
public sealed class BrokerService : SparxieBroker.SparxieBrokerBase
{
    public const string BrokerVersion = "0.1.0";

    private readonly ILogger<BrokerService> _logger;

    public BrokerService(ILogger<BrokerService> logger)
    {
        _logger = logger;
    }

    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        var error = ValidateRequest(request.ProtocolVersion, request.RequestId);
        if (error is not null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Value.Message));
        }

        return Task.FromResult(new PingResponse
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            BrokerVersion = BrokerVersion,
        });
    }

    public override Task<StartSessionResponse> StartSession(StartSessionRequest request, ServerCallContext context)
    {
        var error = ValidateRequest(request.ProtocolVersion, request.RequestId);
        if (error is not null)
        {
            return Task.FromResult(Reject(StageCode.Validation, error.Value));
        }

        var profileError = ProfileSnapshotValidator.Validate(request.Profile);
        if (profileError is not null)
        {
            _logger.LogWarning("StartSession 校验拒绝: {Message}", profileError.Value.Message);
            return Task.FromResult(Reject(StageCode.Validation, profileError.Value));
        }

        // 校验通过。SessionHost 进程创建与私有管道转发在 SessionHost 步骤接线。
        var sessionId = Guid.NewGuid().ToString("N");
        _logger.LogInformation("StartSession 已接受: profile={ProfileId} session={SessionId}",
            request.Profile.ProfileId, sessionId);

        return Task.FromResult(new StartSessionResponse
        {
            Accepted = true,
            SessionId = sessionId,
            Stage = (int)StageCode.Validation,
            ErrorCode = (int)ErrorCode.None,
            Message = "已接受；SessionHost 接线尚未实现",
        });
    }

    public override Task<SetTargetFpsResponse> SetTargetFps(SetTargetFpsRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return Task.FromResult(new SetTargetFpsResponse
            {
                Applied = false,
                Stage = (int)StageCode.Validation,
                ErrorCode = (int)ErrorCode.InvalidArgument,
                Message = "requestId 不能为空",
            });
        }

        if (request.TargetFps is < ProfileSnapshotValidator.MinFps or > ProfileSnapshotValidator.MaxFps)
        {
            return Task.FromResult(new SetTargetFpsResponse
            {
                Applied = false,
                Stage = (int)StageCode.Validation,
                ErrorCode = (int)ErrorCode.InvalidArgument,
                Message = $"targetFps 超出 {ProfileSnapshotValidator.MinFps}–{ProfileSnapshotValidator.MaxFps}",
            });
        }

        // 会话注册表在 SessionHost 步骤接入，当前无会话可路由。
        return Task.FromResult(new SetTargetFpsResponse
        {
            Applied = false,
            Stage = (int)StageCode.HostFault,
            ErrorCode = (int)ErrorCode.SessionNotFound,
            Message = "会话不存在",
        });
    }

    public override async Task StreamEvents(StreamEventsRequest request, IServerStreamWriter<SessionEvent> responseStream, ServerCallContext context)
    {
        var error = ValidateRequest(request.ProtocolVersion, request.RequestId);
        if (error is not null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Value.Message));
        }

        // 事件源在 SessionHost 步骤接入；当前保持流打开直到客户端取消。
        while (!context.CancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static (ErrorCode Code, string Message)? ValidateRequest(int protocolVersion, string requestId)
    {
        if (protocolVersion != RpcContract.ProtocolVersion)
        {
            return (ErrorCode.ProtocolVersionMismatch, $"协议版本不匹配: {protocolVersion} != {RpcContract.ProtocolVersion}");
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return (ErrorCode.InvalidArgument, "requestId 不能为空");
        }

        return null;
    }

    private static StartSessionResponse Reject(StageCode stage, (ErrorCode Code, string Message) error) => new()
    {
        Accepted = false,
        Stage = (int)stage,
        ErrorCode = (int)error.Code,
        Message = error.Message,
    };
}
