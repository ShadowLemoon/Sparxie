using System.Runtime.InteropServices;
using Xunit;

namespace HoyoAbi.Tests;

/// <summary>
/// HoyoTouchCore.dll C ABI 冒烟测试：验证 .NET P/Invoke 契约与 native 导出一致。
/// 测试需先构建 native/HoyoTouchCore（CI 中在 native 构建后运行）。
/// </summary>
public class HoyoAbiSmokeTests
{
    private const uint ABI_VERSION = 1;

    // 与 native adapter/include/hoyo_touch_core_abi.h 保持一致的托管镜像
    private const int HOYO_OK = 0;
    private const int HOYO_ERR_INVALID_ARGUMENT = 1;
    private const int HOYO_ERR_ABI_MISMATCH = 2;
    private const int HOYO_ERR_SESSION_NOT_ACTIVE = 8;
    private const int HOYO_ERR_NOT_SUPPORTED = 9;

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
    private static extern int hoyo_get_abi_version(out uint version, out uint size);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_create_session(ref HoyoLaunchRequest request, ref HoyoResult result, out IntPtr session);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_set_target_fps(IntPtr session, int targetFps, ref HoyoResult result);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_launch(IntPtr session, uint gamePid, ref HoyoResult result);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_wait_game_exit(IntPtr session, uint timeoutMs, ref HoyoResult result);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_release(IntPtr session);

    private static string LocateDll()
    {
        // 测试输出目录或 native 构建输出目录
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "HoyoTouchCore.dll"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "HoyoTouchCore", "build", "Release", "HoyoTouchCore.dll"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.GetFullPath(candidate)))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("未找到 HoyoTouchCore.dll，请先构建 native/HoyoTouchCore");
    }

    private static void EnsureDllAvailable()
    {
        var dll = LocateDll();
        var dest = Path.Combine(AppContext.BaseDirectory, "HoyoTouchCore.dll");
        if (!File.Exists(dest) || !string.Equals(Path.GetFullPath(dll), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(dll, dest, overwrite: true);
        }
    }

    [Fact]
    public void ABI版本查询成功()
    {
        EnsureDllAvailable();
        uint version;
        uint size;
        var rc = hoyo_get_abi_version(out version, out size);
        Assert.Equal(HOYO_OK, rc);
        Assert.Equal(ABI_VERSION, version);
        Assert.Equal((uint)sizeof(uint), size);
    }

    [Fact]
    public void 创建会话成功且句柄非空()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = ABI_VERSION,
            GameType = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 120,
            BackgroundFpsLimitEnabled = 0,
            BackgroundFps = 10,
            ProcessPriority = 1,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out var session);
            Assert.Equal(HOYO_OK, rc);
            Assert.NotEqual(IntPtr.Zero, session);
            Assert.Equal(HOYO_OK, result.ErrorCode);

            // 热调更新
            var fpsResult = new HoyoResult();
            rc = hoyo_set_target_fps(session, 240, ref fpsResult);
            Assert.Equal(HOYO_OK, rc);

            // 越界热调被拒绝
            fpsResult = new HoyoResult();
            rc = hoyo_set_target_fps(session, 9999, ref fpsResult);
            Assert.Equal(HOYO_ERR_INVALID_ARGUMENT, rc);

            // launch 接入 bootstrap：假路径（不存在）应返回扫描/内部失败，而非误报成功
            var launchResult = new HoyoResult();
            rc = hoyo_launch(session, 12345, ref launchResult);
            Assert.NotEqual(HOYO_OK, rc);
            Assert.NotEqual(HOYO_ERR_NOT_SUPPORTED, rc);

            rc = hoyo_release(session);
            Assert.Equal(HOYO_OK, rc);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 非法ABI版本被拒绝()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = 999,
            GameType = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 120,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out _);
            Assert.Equal(HOYO_ERR_ABI_MISMATCH, rc);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void Launch未创建会话被拒绝()
    {
        EnsureDllAvailable();
        var result = new HoyoResult();
        var rc = hoyo_launch(IntPtr.Zero, 12345, ref result);
        Assert.Equal(HOYO_ERR_INVALID_ARGUMENT, rc);
    }

    [Fact]
    public void Launch未激活会话被拒绝()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = ABI_VERSION,
            GameType = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 120,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out var session);
            Assert.Equal(HOYO_OK, rc);
            // 释放会话后再次 launch 同句柄：adapter 会话已失效，返回非 OK（不崩溃即可）
            hoyo_release(session);
            result = new HoyoResult();
            rc = hoyo_launch(session, 0, ref result);
            Assert.NotEqual(HOYO_OK, rc);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void Launch假路径启动失败不误报成功()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = ABI_VERSION,
            GameType = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 120,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
        var result = new HoyoResult();
        try
        {
            hoyo_create_session(ref request, ref result, out var session);
            result = new HoyoResult();
            var rc = hoyo_launch(session, 0, ref result);
            // 假路径（不存在）：bootstrap 创建进程失败或扫描失败，绝不误报成功
            Assert.NotEqual(HOYO_OK, rc);
            Assert.NotEqual(HOYO_ERR_NOT_SUPPORTED, rc);
            hoyo_release(session);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }
}
