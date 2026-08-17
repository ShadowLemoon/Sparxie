using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sparxie.Broker.Hosting;
using Sparxie.Broker.Sessions;
using Sparxie.Broker.Validation;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Zzz;

namespace Sparxie.Broker.Services;

/// <summary>
/// UI/Launcher 只连接 Broker。本服务负责协议/请求校验、会话注册，
/// 为每个 SessionHost 建立私有管道并转发状态、错误与热调请求。
/// </summary>
public sealed class BrokerService : SparxieBroker.SparxieBrokerBase
{
    public const string BrokerVersion = "0.2.0";

    private readonly ILogger<BrokerService> _logger;
    private readonly SessionRegistry _registry;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly BrokerLifecycleOptions _lifecycle;

    public BrokerService(
        ILogger<BrokerService> logger,
        SessionRegistry registry,
        IHostApplicationLifetime lifetime,
        BrokerLifecycleOptions lifecycle)
    {
        _logger = logger;
        _registry = registry;
        _lifetime = lifetime;
        _lifecycle = lifecycle;
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

        if (_registry.HasActiveSessionForGame(request.Profile.Game))
        {
            return Task.FromResult(Reject(StageCode.Mutex, (ErrorCode.MutexConflict, "同款游戏已有活动会话")));
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var pipeName = HostEnvironment.PipeNameFor(sessionId);

        try
        {
            SessionLauncher.Launch(sessionId, request.Profile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 SessionHost 失败");
            return Task.FromResult(Reject(StageCode.CreateProcess, (ErrorCode.ProcessCreateFailed, $"启动 SessionHost 失败: {ex.Message}")));
        }

        var hostSession = new HostSession(sessionId, pipeName, request.Profile.Game, HandleHostEvent);
        _registry.TryAdd(hostSession);

        _logger.LogInformation("StartSession 已接受: profile={ProfileId} session={SessionId}",
            request.Profile.ProfileId, sessionId);

        // 后台连接 Host 私有管道
        _ = Task.Run(() => hostSession.ConnectAsync(CancellationToken.None));

        return Task.FromResult(new StartSessionResponse
        {
            Accepted = true,
            SessionId = sessionId,
            Stage = (int)StageCode.CreateProcess,
            ErrorCode = (int)ErrorCode.None,
            Message = "已接受，正在启动会话",
        });
    }

    public override async Task<SetTargetFpsResponse> SetTargetFps(SetTargetFpsRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return new SetTargetFpsResponse
            {
                Applied = false,
                Stage = (int)StageCode.Validation,
                ErrorCode = (int)ErrorCode.InvalidArgument,
                Message = "requestId 不能为空",
            };
        }

        if (request.TargetFps is < ProfileSnapshotValidator.MinFps or > ProfileSnapshotValidator.MaxFps)
        {
            return new SetTargetFpsResponse
            {
                Applied = false,
                Stage = (int)StageCode.Validation,
                ErrorCode = (int)ErrorCode.InvalidArgument,
                Message = $"targetFps 超出 {ProfileSnapshotValidator.MinFps}–{ProfileSnapshotValidator.MaxFps}",
            };
        }

        if (!_registry.TryGet(request.SessionId, out var session))
        {
            return new SetTargetFpsResponse
            {
                Applied = false,
                Stage = (int)StageCode.HostFault,
                ErrorCode = (int)ErrorCode.SessionNotFound,
                Message = "会话不存在",
            };
        }

        try
        {
            await session.SendCommandAsync(new HostCommand
            {
                Command = "set_target_fps",
                TargetFps = request.TargetFps,
            }, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "转发热调失败: session={SessionId}", request.SessionId);
            return new SetTargetFpsResponse
            {
                Applied = false,
                Stage = (int)StageCode.HostFault,
                ErrorCode = (int)ErrorCode.HostCrashedAfterRunning,
                Message = "Host 不可达，热调失败",
            };
        }

        return new SetTargetFpsResponse
        {
            Applied = true,
            Stage = (int)StageCode.Running,
            ErrorCode = (int)ErrorCode.None,
            Message = "热调已下发",
        };
    }

    public override async Task StreamEvents(
        StreamEventsRequest request,
        IServerStreamWriter<SessionEvent> responseStream,
        ServerCallContext context)
    {
        var error = ValidateRequest(request.ProtocolVersion, request.RequestId);
        if (error is not null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Value.Message));
        }

        var acquired = !_lifecycle.Enabled || _registry.TryAcquireControlStream();
        if (!acquired)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "控制事件流已存在"));
        }

        try
        {
            await foreach (var ev in _registry.Events.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(ev).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_lifecycle.Enabled)
            {
                _registry.ReleaseControlStream();
                RequestStopIfIdle();
            }
        }
    }

    /// <summary>Host 事件处理：写入控制端事件总线；会话结束或 Host 崩溃后移除会话。</summary>
    private async void HandleHostEvent(HostEvent ev)
    {
        _registry.Publish(new SessionEvent
        {
            SessionId = ev.SessionId,
            State = ev.State,
            Stage = ev.Stage,
            ErrorCode = ev.ErrorCode,
            Message = ev.Message,
        });

        HostSession? removedSession = null;
        if (ev.State is "Exited" or "Failed" or "HostCrashedBeforeRunning" or "HostCrashedAfterRunning")
        {
            removedSession = _registry.Remove(ev.SessionId);
            if (removedSession is not null)
            {
                await removedSession.DisposeAsync().ConfigureAwait(false);
            }
        }

        var isZzz = removedSession is not null
            && string.Equals(removedSession.Game, "zenlessZoneZero", StringComparison.Ordinal);

        // ZZZ Host 在 Running 前异常死亡：本次会话立即走共享恢复例程恢复 PC 配置。
        // 只处理 Running 前崩溃；Running 后恢复记录应已删除，不重建。
        if (ev.State == "HostCrashedBeforeRunning" && isZzz)
        {
            await TryRecoverZzzConfigAsync(ev.SessionId).ConfigureAwait(false);
        }

        RequestStopIfIdle();
    }

    private void RequestStopIfIdle()
    {
        if (_lifecycle.Enabled && !_registry.HasControlStream && !_registry.HasActiveSessions)
        {
            _lifetime.StopApplication();
        }
    }

    /// <summary>共享恢复例程：存在恢复记录时执行；成功或无需恢复都算正常收尾。</summary>
    private async Task TryRecoverZzzConfigAsync(string sessionId)
    {
        try
        {
            var record = ZzzRecoveryStore.TryLoad(sessionId);
            if (record is null)
            {
                return;
            }

            // 确认本次游戏进程已退出（临时 Job 已终止），再由恢复例程校验并恢复。
            ZzzRecoveryStore.Restore(record);
            _logger.LogInformation("ZZZ 配置已在本次会话恢复: session={SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            // 恢复失败或无法确认游戏退出：保留恢复资产，由下次启动兜底。
            _logger.LogError(ex, "ZZZ 配置本次恢复失败，遗留记录留给下次启动兜底: session={SessionId}", sessionId);
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
