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
                _gameProcess = await StartGameProcessAsync(cancellationToken).ConfigureAwait(false);
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

            RestoreGameRegistrySettings();
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

    private GameRegistryBackup? _registryBackup;

    /// <summary>备份落盘位置（按游戏区分）：进程被强杀 / 断电后，下次启动先用它把游戏设置恢复回去。</summary>
    private string BackupFile => Path.Combine(
        Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve(), "recovery", $"registry-backup-{_options.Profile.Game}.json");

    /// <summary>
    /// 启动前：先备份游戏注册表里我们要改的分辨率 / 全屏值，再写入这套配置存的值；游戏进程退出后由 <see cref="RestoreGameRegistrySettings"/> 恢复。
    /// 这样触屏启动用它自己的一套，键鼠启动（官方启动器等）看到的还是原来的值。返回给 Starting 事件的说明（没有就返回 null）。
    /// </summary>
    private string? ApplyGameRegistrySettings()
    {
        try
        {
            RestoreGameRegistrySettings(includeLeftover: true); // 上次异常退出遗留的备份先恢复，免得把我们自己写的值当成“原值”备份
            var registry = new CurrentUserGameRegistry();
            var (result, backup) = GameRegistrySettings.ApplyWithBackup(
                _options.Profile.ToGameType(), _options.Profile.ExecutablePath, _options.Profile.Launch.ToModel(), registry);
            if (result.Applied && !backup.IsEmpty)
            {
                _registryBackup = backup;
                Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
                File.WriteAllText(BackupFile, backup.ToJson());
            }

            return result.Note ?? (result.Applied ? $"已写入游戏设置（{result.WrittenKeys.Count} 处），退出后恢复" : null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"写入游戏设置失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把本次会话的备份值写回注册表，可重复调用。<paramref name="includeLeftover"/> 为 true 时（只在确认游戏没在运行的启动前）
    /// 才处理磁盘上上次遗留的备份；收尾路径不能碰它——被拒绝的第二个会话（游戏已在运行）收尾时，
    /// 磁盘上那份属于正在运行的第一个会话，不能被它提前恢复掉。
    /// </summary>
    private void RestoreGameRegistrySettings(bool includeLeftover = false)
    {
        try
        {
            var backup = _registryBackup;
            if (backup is null && includeLeftover && File.Exists(BackupFile))
            {
                backup = GameRegistryBackup.FromJson(File.ReadAllText(BackupFile));
            }

            backup?.Restore(new CurrentUserGameRegistry());
            if (backup is not null || includeLeftover)
            {
                _registryBackup = null;
                if (File.Exists(BackupFile))
                {
                    File.Delete(BackupFile);
                }
            }
        }
        catch (Exception)
        {
            // 恢复失败（权限、路径、注册表、备份文件损坏…任何原因）：保留备份文件，下次启动前再试一次。
            // 这是会话收尾路径，绝不能因此抛出而冲掉已经发出的终态事件。
        }
    }

    private async Task<Process> StartGameProcessAsync(CancellationToken cancellationToken)
    {
        // 绝区零 Steam 版：经 Steam 拉起（目录里有 steam_appid.txt），其余直接运行 EXE。
        if (_options.Profile.ToGameType() == GameType.ZenlessZoneZero && SteamLaunch.IsSteamInstall(_options.Profile.ExecutablePath))
        {
            return await SteamLaunch.StartAsync(
                SteamLaunch.ZenlessZoneZeroAppId,
                _options.Profile.ExecutablePath,
                LaunchArguments.Build(_options.Profile.Launch.ToModel()),
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);
        }

        return StartGameProcess();
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

        // 游戏进程在 Running 前就失败 / 被杀时也要把设置恢复回去（已恢复过则什么都不做）。
        if (!_announcedRunning || (_gameProcess?.HasExited ?? false))
        {
            RestoreGameRegistrySettings();
        }

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
            // 字段缺省（旧客户端）= 触屏开启，保持既有行为。
            TouchUiEnabled = !hoyo.HasTouchUiEnabled || hoyo.TouchUiEnabled,
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
