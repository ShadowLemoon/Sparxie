using System.Diagnostics;
using Sparxie.Contracts.Errors;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Jobs;
using Sparxie.Infrastructure.Processes;
using Sparxie.SessionHost.Hosting;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// 单局游戏会话状态机：互斥、已运行检测、Running 前失效保护、
/// Runtime 安装、Running 撤销 kill-on-close、等待退出与清理。
/// 只处置本次由 Host 创建的进程。
/// </summary>
public sealed class GameSession : IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly IGameController _controller;
    private readonly Func<HostEvent, Task> _onEvent;

    private Mutex? _mutex;
    private PreRunningJob? _job;
    private Process? _gameProcess;
    private bool _announcedRunning;

    public GameSession(HostOptions options, IGameController controller, Func<HostEvent, Task> onEvent)
    {
        _options = options;
        _controller = controller;
        _onEvent = onEvent;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidate(out var error))
            {
                await FailAsync(error, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TryAcquireMutex(out error))
            {
                await FailAsync(error, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (GameProcessDetector.IsGameRunning(_options.Profile.ToGameType()))
            {
                await FailAsync((StageCode.Mutex, ErrorCode.GameAlreadyRunning, "对应游戏已在运行，拒绝接管"), cancellationToken).ConfigureAwait(false);
                return;
            }

            // 启动设置：分辨率 / 窗口模式写入游戏自己的注册表设置（尽力而为，失败不阻止启动）。
            var launchNote = ApplyGameRegistrySettings();

            // 启动游戏进程前的 Runtime 准备（ZZZ：备份 PC 配置并写入触屏配置）
            await _controller.PrepareLaunchAsync(_options.Profile, cancellationToken).ConfigureAwait(false);

            // Running 前失效保护 Job：所有游戏在 Running 前都要覆盖本次进程。
            // Hoyo 由 bootstrap 创建进程后 Assign；其余由 SessionHost 预创建并 Assign。
            _job = PreRunningJob.Create();
            if (!_controller.CreatesProcess)
            {
                _gameProcess = StartGameProcess();
                _job.Assign(_gameProcess.SafeHandle);
            }

            await EmitAsync(SessionStates.Starting, StageCode.CreateProcess, ErrorCode.None, launchNote, cancellationToken).ConfigureAwait(false);

            // Runtime 安装/注入：全部成功才进入 Running
            await _controller.InstallAsync(_gameProcess, _job.JobHandle, _options.Profile.Hoyo.ToModel(), cancellationToken).ConfigureAwait(false);

            // 注入成功后、Running 前的配置收尾（ZZZ：恢复 PC 文件配置并删除恢复记录）
            await _controller.PostInstallAsync(cancellationToken).ConfigureAwait(false);

            // 进入 Running 前撤销 kill-on-close，转换失败即整次失败
            if (_job is not null)
            {
                try
                {
                    _job.ReleaseKillOnClose();
                }
                catch (Exception ex)
                {
                    await FailAsync((StageCode.Job, ErrorCode.JobSetupFailed, $"撤销 Running 前失效保护失败: {ex.Message}"), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            _announcedRunning = true;
            await EmitAsync(SessionStates.Running, StageCode.Running, ErrorCode.None, null, cancellationToken).ConfigureAwait(false);
            var runningSince = DateTimeOffset.UtcNow;

            int? exitCode = null;
            if (_gameProcess is not null)
            {
                await _gameProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                exitCode = TryGetExitCode(_gameProcess);
            }
            else
            {
                // Hoyo：Runtime 创建的进程由 controller 等待退出
                await _controller.WaitExitAsync(cancellationToken).ConfigureAwait(false);
            }

            await EmitAsync(SessionStates.Exited, StageCode.Wait, ErrorCode.None,
                GameExitDescription.Describe(exitCode, DateTimeOffset.UtcNow - runningSince), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FailAsync((StageCode.Cleanup, ErrorCode.None, "会话被取消"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailAsync((StageCode.Cleanup, ErrorCode.RuntimeLoadFailed, ex.Message), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Cleanup();

            // 失败路径收尾（Hoyo 释放 native 会话；ZZZ 不再有配置需要回滚）
            try
            {
                await _controller.AbortAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 收尾失败不吞掉，但不再改变会话终态
                await EmitAsync(SessionStates.Failed, StageCode.Cleanup, ErrorCode.ZzzRecoveryFailed, $"会话收尾失败: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public bool IsRunning => _announcedRunning;

    public void SetTargetFps(int targetFps)
    {
        if (!_announcedRunning)
        {
            throw new InvalidOperationException("会话尚未进入 Running");
        }

        _controller.SetTargetFpsAsync(targetFps, CancellationToken.None).GetAwaiter().GetResult();
    }

    private bool TryValidate(out (StageCode Stage, ErrorCode Code, string Message) error)
    {
        var profile = _options.Profile;

        if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
        {
            error = (StageCode.Validation, ErrorCode.InvalidExecutableName, "executablePath 为空");
            return false;
        }

        if (!File.Exists(profile.ExecutablePath))
        {
            error = (StageCode.Validation, ErrorCode.ExecutableNotFound, $"游戏 EXE 不存在: {profile.ExecutablePath}");
            return false;
        }

        if (!GameExecutables.IsAllowed(profile.ToGameType(), profile.ExecutablePath))
        {
            error = (StageCode.Validation, ErrorCode.InvalidExecutableName, $"EXE 名称不在白名单: {Path.GetFileName(profile.ExecutablePath)}");
            return false;
        }

        error = default;
        return true;
    }

    private bool TryAcquireMutex(out (StageCode Stage, ErrorCode Code, string Message) error)
    {
        var name = $"Local\\Sparxie.Mutex.{_options.Profile.ToGameType().ToString().ToLowerInvariant()}";
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);

        if (!createdNew)
        {
            try
            {
                if (!_mutex.WaitOne(0))
                {
                    _mutex.Dispose();
                    _mutex = null;
                    error = (StageCode.Mutex, ErrorCode.MutexConflict, "同款游戏已有活动会话");
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // 前持有者崩溃，互斥已 abandoned，当前实例已获得所有权
            }
        }

        error = default;
        return true;
    }

    /// <summary>把分辨率 / 窗口模式写入游戏注册表设置；返回给 Starting 事件的说明（没有设置返回 null）。</summary>
    private string? ApplyGameRegistrySettings()
    {
        var launch = _options.Profile.Launch.ToModel();
        if (launch is null)
        {
            return null;
        }

        try
        {
            var result = GameRegistrySettings.Apply(
                _options.Profile.ToGameType(), _options.Profile.ExecutablePath, launch, new CurrentUserGameRegistry());
            return result.Note ?? (result.Applied ? $"已写入游戏设置（{result.WrittenKeys.Count} 处）" : null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"写入游戏设置失败：{ex.Message}";
        }
    }

    private Process StartGameProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.Profile.ExecutablePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(_options.Profile.ExecutablePath) ?? string.Empty,
            // 自动注入的无边框参数 + 用户附加参数（Hoyo 由 native bootstrap 创建进程，参数经 C ABI 传入）。
            Arguments = LaunchArguments.Build(_options.Profile.Launch.ToModel()),
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("CreateProcess 返回 null");

        return process;
    }

    private async Task EmitAsync(string state, StageCode stage, ErrorCode code, string? message, CancellationToken cancellationToken)
    {
        await _onEvent(new HostEvent
        {
            SessionId = _options.SessionId,
            State = state,
            Stage = (int)stage,
            ErrorCode = (int)code,
            Message = message ?? string.Empty,
        }).ConfigureAwait(false);
    }

    private async Task FailAsync((StageCode Stage, ErrorCode Code, string Message) error, CancellationToken cancellationToken)
    {
        await EmitAsync(SessionStates.Failed, error.Stage, error.Code, error.Message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统一清理：只处置本次由 Host 创建且尚未宣布 Running 的进程；Running 后保留游戏。</summary>
    private void Cleanup()
    {
        _controller.Dispose();

        if (_gameProcess is not null)
        {
            if (!_announcedRunning)
            {
                try
                {
                    if (!_gameProcess.HasExited)
                    {
                        _gameProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // 进程可能已退出
                }
            }

            _gameProcess.Dispose();
            _gameProcess = null;
        }

        _job?.Dispose();
        _job = null;

        _mutex?.Dispose();
        _mutex = null;
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }
}

internal static class ProfileSnapshotExtensions
{
    public static GameType ToGameType(this ProfileSnapshot profile) => profile.Game switch
    {
        "genshin" => GameType.Genshin,
        "starRail" => GameType.StarRail,
        "zenlessZoneZero" => GameType.ZenlessZoneZero,
        _ => throw new InvalidOperationException($"未知 game: {profile.Game}"),
    };

    /// <summary>快照里的 launch（0 / 空串表示“不修改”）→ 模型；没有任何设置返回 null。</summary>
    public static Sparxie.Contracts.Models.LaunchSettings? ToModel(this Sparxie.Contracts.Rpc.LaunchSettings? launch)
    {
        if (launch is null)
        {
            return null;
        }

        var model = new Sparxie.Contracts.Models.LaunchSettings
        {
            Width = launch.Width > 0 ? launch.Width : null,
            Height = launch.Height > 0 ? launch.Height : null,
            Fullscreen = launch.Fullscreen switch { 1 => true, 2 => false, _ => null },
            Borderless = launch.Borderless,
            ExtraArguments = string.IsNullOrWhiteSpace(launch.ExtraArguments) ? null : launch.ExtraArguments,
        };
        return model.IsEmpty ? null : model;
    }

    public static HoyoProfileSettings? ToModel(this HoyoSettings? hoyo)
    {
        if (hoyo is null)
        {
            return null;
        }

        return new HoyoProfileSettings
        {
            FpsUnlockEnabled = hoyo.FpsUnlockEnabled,
            TargetFps = hoyo.TargetFps,
            BackgroundFpsLimitEnabled = hoyo.BackgroundFpsLimitEnabled,
            BackgroundFps = hoyo.BackgroundFps,
            ProcessPriority = hoyo.ProcessPriority switch
            {
                "belowNormal" => ProcessPriority.BelowNormal,
                "aboveNormal" => ProcessPriority.AboveNormal,
                "high" => ProcessPriority.High,
                _ => ProcessPriority.Normal,
            },
            GenshinFollowInGamePreset = hoyo.GenshinFollowInGamePreset,
            GenshinPreset30Fps = hoyo.GenshinPreset30Fps,
            GenshinPreset60Fps = hoyo.GenshinPreset60Fps,
            GenshinTouchUiScaleOverrideEnabled = hoyo.GenshinTouchUiScaleOverrideEnabled,
            GenshinTouchUiScalePercent = hoyo.GenshinTouchUiScalePercent,
        };
    }
}
