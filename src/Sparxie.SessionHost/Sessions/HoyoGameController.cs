using System.Diagnostics;
using System.Runtime.InteropServices;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// Hoyo（原神/星铁）Runtime 控制器：经 HoyoTouchCore.dll C ABI 创建会话、
/// 启动注入、热调与释放。所有步骤全部成功才进入 Running；失败整次失败。
/// </summary>
public sealed class HoyoGameController : IGameController
{
    private const uint ABI_VERSION = 1;
    private const int HOYO_OK = 0;

    private IntPtr _session;
    private bool _disposed;

    public async Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken)
    {
        // Hoyo 无启动前配置切换；会话在校验后由 InstallAsync 创建。
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task InstallAsync(Process gameProcess, HoyoProfileSettings? hoyo, CancellationToken cancellationToken)
    {
        var request = BuildRequest(gameProcess, hoyo);
        var result = new HoyoResult { Size = (uint)Marshal.SizeOf<HoyoResult>() };

        try
        {
            var createRc = hoyo_create_session(ref request, ref result, out var session);
            if (createRc != HOYO_OK)
            {
                throw new InvalidOperationException($"Hoyo 会话创建失败: {createRc} {Marshal.PtrToStringUni(result.Message)}");
            }

            _session = session;
            result = new HoyoResult { Size = (uint)Marshal.SizeOf<HoyoResult>() };
            var launchRc = hoyo_launch(session, (uint)gameProcess.Id, ref result);
            if (launchRc != HOYO_OK)
            {
                hoyo_release(session);
                _session = IntPtr.Zero;
                throw new InvalidOperationException($"Hoyo 启动注入失败: {launchRc} {Marshal.PtrToStringUni(result.Message)}");
            }

        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task PostInstallAsync(CancellationToken cancellationToken)
    {
        // Hoyo 无注入后配置收尾
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task AbortAsync(CancellationToken cancellationToken)
    {
        // 失败路径：释放 native 会话（游戏进程由 GameSession 终止）
        if (_session != IntPtr.Zero)
        {
            hoyo_release(_session);
            _session = IntPtr.Zero;
        }

        return Task.CompletedTask;
    }

    public async Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken)
    {
        if (_session == IntPtr.Zero)
        {
            throw new InvalidOperationException("Hoyo 会话未激活");
        }

        var result = new HoyoResult { Size = (uint)Marshal.SizeOf<HoyoResult>() };
        var rc = hoyo_set_target_fps(_session, targetFps, ref result);
        if (rc != HOYO_OK)
        {
            throw new InvalidOperationException($"FPS 热调失败: {rc} {Marshal.PtrToStringUni(result.Message)}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session != IntPtr.Zero)
        {
            hoyo_release(_session);
            _session = IntPtr.Zero;
        }
    }

    private static HoyoLaunchRequest BuildRequest(Process gameProcess, HoyoProfileSettings? hoyo)
    {
        var settings = hoyo ?? new HoyoProfileSettings { FpsUnlockEnabled = true, TargetFps = 120, ProcessPriority = ProcessPriority.Normal };
        var path = gameProcess.MainModule?.FileName ?? string.Empty;

        return new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = ABI_VERSION,
            GameType = 0, // genshin/starRail 统一走 Hoyo 上游流程，game_type 由 adapter 后续细分
            FpsUnlockEnabled = settings.FpsUnlockEnabled ? 1 : 0,
            TargetFps = settings.TargetFps,
            BackgroundFpsLimitEnabled = settings.BackgroundFpsLimitEnabled ? 1 : 0,
            BackgroundFps = settings.BackgroundFps,
            ProcessPriority = (int)settings.ProcessPriority,
            GenshinFollowInGamePreset = settings.GenshinFollowInGamePreset ? 1 : 0,
            GenshinPreset30Fps = settings.GenshinPreset30Fps,
            GenshinPreset60Fps = settings.GenshinPreset60Fps,
            GenshinTouchUiScaleOverrideEnabled = settings.GenshinTouchUiScaleOverrideEnabled ? 1 : 0,
            GenshinTouchUiScalePercent = settings.GenshinTouchUiScalePercent,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HoyoLaunchRequest
    {
        public uint Size;
        public uint AbiVersion;
        public int GameType;
        public int FpsUnlockEnabled;
        public int TargetFps;
        public int BackgroundFpsLimitEnabled;
        public int BackgroundFps;
        public int ProcessPriority;
        public int GenshinFollowInGamePreset;
        public int GenshinPreset30Fps;
        public int GenshinPreset60Fps;
        public int GenshinTouchUiScaleOverrideEnabled;
        public int GenshinTouchUiScalePercent;
        public IntPtr GameExecutablePath;
        public uint GameExecutablePathChars;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HoyoResult
    {
        public uint Size;
        public int ErrorCode;
        public uint Stage;
        public uint Detail;
        public IntPtr Message;
        public uint MessageChars;
    }

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_create_session(ref HoyoLaunchRequest request, ref HoyoResult result, out IntPtr session);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_launch(IntPtr session, uint gamePid, ref HoyoResult result);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_set_target_fps(IntPtr session, int targetFps, ref HoyoResult result);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_release(IntPtr session);
}
