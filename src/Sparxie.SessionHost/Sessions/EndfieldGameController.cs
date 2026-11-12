using System.Diagnostics;
using System.Runtime.InteropServices;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// Endfield 启动与运行时控制器。游戏按 Collapse Endfield 插件的方式启动：以挂起的 Games.exe 作为
/// 父进程创建 Endfield.exe（PROC_THREAD_ATTRIBUTE_PARENT_PROCESS，见 native/EndfieldCore/endfield_decoy.cpp），
/// 随后注入独立 Runtime，由 Runtime 通过公开的 IL2CPP 导出调用输入模式切换与 Unity 帧率接口。
/// Games.exe 与游戏同属会话 Job，游戏退出时由 EndfieldCore 结束 Games.exe 并清理暂存目录。
/// </summary>
public sealed class EndfieldGameController : IGameController
{
    private const int Ok = 0;
    private IntPtr _session;
    private string? _launchPath;
    private bool _disposed;

    public bool CreatesProcess => true;

    public Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken)
    {
        _launchPath = profile.ExecutablePath;
        return Task.CompletedTask;
    }

    public Task InstallAsync(Process? gameProcess, IntPtr jobHandle, HoyoProfileSettings? hoyo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_launchPath))
        {
            throw new InvalidOperationException("Endfield EXE 路径为空");
        }

        var request = BuildRequest(_launchPath, hoyo);
        var result = NewResult();
        try
        {
            var rc = endfield_create_session(ref request, ref result, out _session);
            ThrowIfFailed(rc, result, "Endfield 会话创建失败");

            if (jobHandle != IntPtr.Zero)
            {
                result = NewResult();
                rc = endfield_attach_job(_session, jobHandle, ref result);
                ThrowIfFailed(rc, result, "Endfield Job 绑定失败");
            }

            result = NewResult();
            rc = endfield_launch(_session, ref result);
            ThrowIfFailed(rc, result, $"Endfield 启动注入失败（阶段 {result.Stage}）");
            WriteLaunchLine();
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }

        return Task.CompletedTask;
    }

    public Task PostInstallAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AbortAsync(CancellationToken cancellationToken)
    {
        Release();
        return Task.CompletedTask;
    }

    public async Task WaitExitAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = NewResult();
            var rc = endfield_wait_game_exit(_session, 500, ref result);
            if (rc == Ok)
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SetTargetFpsAsync(int targetFps, CancellationToken cancellationToken)
    {
        var result = NewResult();
        var rc = endfield_set_target_fps(_session, targetFps, ref result);
        ThrowIfFailed(rc, result, "Endfield FPS 热调失败");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }

    /// <summary>One line per launch into logs\endfield-launch.log: the game pid, the decoy parent and the parent the OS reports.</summary>
    private void WriteLaunchLine()
    {
        try
        {
            var info = new EndfieldSessionInfo { Size = (uint)Marshal.SizeOf<EndfieldSessionInfo>() };
            if (endfield_get_session_info(_session, ref info) != Ok)
            {
                return;
            }

            var directory = Path.Combine(Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve(), "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "endfield-launch.log"),
                $"[{DateTime.Now:O}] game pid={info.GamePid} decoy parent pid={info.DecoyPid} parent seen by the OS={info.ParentPidSeen} image={Marshal.PtrToStringUni(info.DecoyImage, (int)info.DecoyImageChars)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // a diagnostics line must never fail a launch
        }
    }

    private void Release()
    {
        if (_session != IntPtr.Zero)
        {
            endfield_release(_session);
            _session = IntPtr.Zero;
        }
    }

    private static EndfieldResult NewResult() => new() { Size = (uint)Marshal.SizeOf<EndfieldResult>() };

    internal static EndfieldLaunchRequest BuildRequest(string launchPath, HoyoProfileSettings? hoyo)
    {
        var settings = hoyo ?? new HoyoProfileSettings();
        var width = settings.EndfieldAutoResolution ? GetSystemMetrics(0) : settings.EndfieldWidth;
        var height = settings.EndfieldAutoResolution ? GetSystemMetrics(1) : settings.EndfieldHeight;
        return new EndfieldLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<EndfieldLaunchRequest>(),
            AbiVersion = AbiVersion,
            FpsUnlockEnabled = settings.FpsUnlockEnabled ? 1 : 0,
            TargetFps = settings.TargetFps,
            TouchUiEnabled = settings.EndfieldInputMode is "pc" ? 0 : 1,
            Width = width > 0 ? width : settings.EndfieldWidth,
            Height = height > 0 ? height : settings.EndfieldHeight,
            RefreshRate = settings.EndfieldRefreshRate,
            GameExecutablePath = Marshal.StringToHGlobalUni(launchPath),
            GameExecutablePathChars = (uint)launchPath.Length,
            // flags 0 = production: Games.exe from the game's folder (PlatformProcess.exe), signatures required
            Flags = 0,
            ParentSourcePath = IntPtr.Zero,
            ParentSourcePathChars = 0,
        };
    }

    private static void ThrowIfFailed(int code, EndfieldResult result, string prefix)
    {
        if (code == Ok)
        {
            return;
        }

        var detail = Marshal.PtrToStringUni(result.Message);
        throw new InvalidOperationException($"{prefix}: {code} {detail}");
    }

    private const uint AbiVersion = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    internal struct EndfieldLaunchRequest
    {
        public uint Size;
        public uint AbiVersion;
        public int FpsUnlockEnabled;
        public int TargetFps;
        public int TouchUiEnabled;
        public int Width;
        public int Height;
        public int RefreshRate;
        public IntPtr GameExecutablePath;
        public uint GameExecutablePathChars;
        // ENDFIELD_REQUEST_SIZE_V1 (48 bytes) ends here; the fields below exist since the decoy-parent launch.
        public uint Flags;
        public uint ParentSourcePathChars;
        public IntPtr ParentSourcePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EndfieldSessionInfo
    {
        public uint Size;
        public uint GamePid;
        public uint DecoyPid;
        public uint ParentPidSeen;
        public IntPtr DecoyImage;
        public uint DecoyImageChars;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EndfieldResult
    {
        public uint Size;
        public int ErrorCode;
        public uint Stage;
        public uint Detail;
        public IntPtr Message;
        public uint MessageChars;
    }

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_create_session(ref EndfieldLaunchRequest request, ref EndfieldResult result, out IntPtr session);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_attach_job(IntPtr session, IntPtr jobHandle, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_launch(IntPtr session, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_get_session_info(IntPtr session, ref EndfieldSessionInfo info);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_set_target_fps(IntPtr session, int targetFps, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_wait_game_exit(IntPtr session, uint timeoutMs, ref EndfieldResult result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_release(IntPtr session);
}
