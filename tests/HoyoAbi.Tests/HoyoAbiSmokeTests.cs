using System.Runtime.InteropServices;
using Xunit;

namespace HoyoAbi.Tests;

/// <summary>
/// HoyoTouchCore.dll C ABI 冒烟测试：验证 .NET P/Invoke 契约与 native 导出一致。
/// 测试需先构建 native/HoyoTouchCore（CI 中在 native 构建后运行）。
/// </summary>
public class HoyoAbiSmokeTests
{
    private const uint ABI_VERSION = 2;

    // 与 native adapter/include/hoyo_touch_core_abi.h 保持一致的托管镜像
    private const int HOYO_OK = 0;
    private const int HOYO_ERR_INVALID_ARGUMENT = 1;
    private const int HOYO_ERR_ABI_MISMATCH = 2;
    private const int HOYO_ERR_CONFIG_INVALID = 3;
    private const uint HOYO_STAGE_SCAN_TOUCH = 2;
    private const uint HOYO_STAGE_LOAD_ENGINE = 7;
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
        public IntPtr GameArguments;
        public uint GameArgumentsChars;
        public int TouchUiDisabled;
        public int Reserved0;
    }

    // 追加 touch_ui_disabled 之前的布局（x64：88 字节）：旧 SessionHost 的请求结构。
    [StructLayout(LayoutKind.Sequential)]
    private struct HoyoLaunchRequestV2
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
        public IntPtr GameArguments;
        public uint GameArgumentsChars;
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

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "hoyo_create_session")]
    private static extern int hoyo_create_session_v2(ref HoyoLaunchRequestV2 request, ref HoyoResult result, out IntPtr session);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_create_session(ref HoyoLaunchRequest request, ref HoyoResult result, out IntPtr session);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_attach_job(IntPtr session, IntPtr jobHandle, ref HoyoResult result);

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
        // 优先 native 构建输出目录（新产物），测试输出目录仅作兜底，
        // 避免"源=目标"导致永远复制不到新构建的 DLL。
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "HoyoTouchCore", "build", "Release", "HoyoTouchCore.dll"),
            Path.Combine(AppContext.BaseDirectory, "HoyoTouchCore.dll"),
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

    private static string? _preparedSourceHash;

    private static void EnsureDllAvailable()
    {
        var dll = LocateDll();
        var sourceHash = HashFile(dll);
        var dest = Path.Combine(AppContext.BaseDirectory, "HoyoTouchCore.dll");

        // 已就绪（同源且存在）则跳过；避免 DLL 被 testhost 加载锁定后重复复制失败
        if (_preparedSourceHash == sourceHash && File.Exists(dest))
        {
            return;
        }

        File.Copy(dll, dest, overwrite: true);
        _preparedSourceHash = sourceHash;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash);
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
    public void 带启动参数的会话可创建且对假路径启动失败而不崩溃()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        const string args = "-popupwindow -screen-width 1280";
        var request = new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = ABI_VERSION,
            GameType = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 120,
            ProcessPriority = 1,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
            GameArguments = Marshal.StringToHGlobalUni(args),
            GameArgumentsChars = (uint)args.Length,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out var session);
            Assert.Equal(HOYO_OK, rc);

            // 假路径：bootstrap 创建进程失败 -> 非 OK，且不会误报成功（参数已随请求传到 native 侧）。
            var launchResult = new HoyoResult();
            rc = hoyo_launch(session, 0, ref launchResult);
            Assert.NotEqual(HOYO_OK, rc);

            Assert.Equal(HOYO_OK, hoyo_release(session));
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
            Marshal.FreeHGlobal(request.GameArguments);
        }
    }

    [Fact]
    public void 启动参数长度非零但指针为空被拒绝()
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
            GameArguments = IntPtr.Zero,
            GameArgumentsChars = 5,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out _);
            Assert.Equal(HOYO_ERR_INVALID_ARGUMENT, rc);
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
    public void AttachJob未创建会话被拒绝()
    {
        EnsureDllAvailable();
        var result = new HoyoResult();
        var rc = hoyo_attach_job(IntPtr.Zero, new IntPtr(0x1234), ref result);
        Assert.Equal(HOYO_ERR_INVALID_ARGUMENT, rc);
    }

    [Fact]
    public void AttachJob正常会话绑定成功()
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
            Assert.NotEqual(IntPtr.Zero, session);

            // 任意非零句柄仅做传递验证（不实际 Assign 进程）
            result = new HoyoResult();
            rc = hoyo_attach_job(session, new IntPtr(0x1), ref result);
            Assert.Equal(HOYO_OK, rc);

            rc = hoyo_release(session);
            Assert.Equal(HOYO_OK, rc);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
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

    [Fact]
    public void 热调FPS边界值10与1000通过()
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

            foreach (var fps in new[] { 10, 1000 })
            {
                var fpsResult = new HoyoResult();
                rc = hoyo_set_target_fps(session, fps, ref fpsResult);
                Assert.Equal(HOYO_OK, rc);
            }

            // 越界 9 / 1001 被拒绝
            foreach (var fps in new[] { 9, 1001 })
            {
                var fpsResult = new HoyoResult();
                rc = hoyo_set_target_fps(session, fps, ref fpsResult);
                Assert.Equal(HOYO_ERR_INVALID_ARGUMENT, rc);
            }

            hoyo_release(session);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 纯触屏会话创建成功()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequest
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
            AbiVersion = ABI_VERSION,
            GameType = 1,
            FpsUnlockEnabled = 0, // 纯触屏：FPS 解锁关闭
            TargetFps = 120,
            BackgroundFpsLimitEnabled = 0,
            BackgroundFps = 10,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out var session);
            Assert.Equal(HOYO_OK, rc);
            Assert.NotEqual(IntPtr.Zero, session);

            // 纯触屏下热调仍可设值（仅记录目标；bootstrap 不安装 FPS Patch）
            var fpsResult = new HoyoResult();
            rc = hoyo_set_target_fps(session, 60, ref fpsResult);
            Assert.Equal(HOYO_OK, rc);

            hoyo_release(session);
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    // ---- touch_ui_enabled（PC 界面会话）----

    private static HoyoLaunchRequest NewRequest(string path, int touchUiDisabled, int fps, int gameType = 1) => new()
    {
        Size = (uint)Marshal.SizeOf<HoyoLaunchRequest>(),
        AbiVersion = ABI_VERSION,
        GameType = gameType,
        FpsUnlockEnabled = fps,
        TargetFps = 120,
        BackgroundFps = 10,
        ProcessPriority = 1,
        TouchUiDisabled = touchUiDisabled,
        GameExecutablePath = Marshal.StringToHGlobalUni(path),
        GameExecutablePathChars = (uint)path.Length,
    };

    [Fact]
    public void 请求结构大小为96字节且每个字段偏移与C头的offsetof一致()
    {
        // hoyo_touch_core_abi.h 的 offsetof 值（x64，头文件里有同样数值的 static_assert）：偏移错位时 native 会从别的字段里读 touch_ui_disabled 和 game_arguments
        var expected = new (string Field, int Offset)[]
        {
            (nameof(HoyoLaunchRequest.Size), 0),
            (nameof(HoyoLaunchRequest.AbiVersion), 4),
            (nameof(HoyoLaunchRequest.GameType), 8),
            (nameof(HoyoLaunchRequest.FpsUnlockEnabled), 12),
            (nameof(HoyoLaunchRequest.TargetFps), 16),
            (nameof(HoyoLaunchRequest.BackgroundFpsLimitEnabled), 20),
            (nameof(HoyoLaunchRequest.BackgroundFps), 24),
            (nameof(HoyoLaunchRequest.ProcessPriority), 28),
            (nameof(HoyoLaunchRequest.GenshinFollowInGamePreset), 32),
            (nameof(HoyoLaunchRequest.GenshinPreset30Fps), 36),
            (nameof(HoyoLaunchRequest.GenshinPreset60Fps), 40),
            (nameof(HoyoLaunchRequest.GenshinTouchUiScaleOverrideEnabled), 44),
            (nameof(HoyoLaunchRequest.GenshinTouchUiScalePercent), 48),
            (nameof(HoyoLaunchRequest.GameExecutablePath), 56),
            (nameof(HoyoLaunchRequest.GameExecutablePathChars), 64),
            (nameof(HoyoLaunchRequest.GameArguments), 72),
            (nameof(HoyoLaunchRequest.GameArgumentsChars), 80),
            (nameof(HoyoLaunchRequest.TouchUiDisabled), 84),
            (nameof(HoyoLaunchRequest.Reserved0), 88),
        };
        Assert.Equal(96, Marshal.SizeOf<HoyoLaunchRequest>());
        Assert.Equal(88, Marshal.SizeOf<HoyoLaunchRequestV2>());
        foreach (var (field, offset) in expected)
        {
            Assert.Equal(offset, (int)Marshal.OffsetOf<HoyoLaunchRequest>(field));
        }
    }

    [Theory]
    [InlineData(0, 1)] // 触屏 + FPS 解锁（既有行为）
    [InlineData(1, 1)] // PC 界面 + FPS 解锁
    [InlineData(0, 0)] // 纯触屏（既有行为）
    [InlineData(1, 0)] // 没有可安装内容：被拒绝
    public void 触屏开关组合的会话创建(int touchUiDisabled, int fps)
    {
        EnsureDllAvailable();
        var request = NewRequest(Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe"), touchUiDisabled, fps);
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session(ref request, ref result, out var session);
            if (touchUiDisabled == 1 && fps == 0)
            {
                Assert.Equal(HOYO_ERR_CONFIG_INVALID, rc);
                Assert.Equal(IntPtr.Zero, session);
            }
            else
            {
                Assert.Equal(HOYO_OK, rc);
                Assert.Equal(HOYO_OK, hoyo_release(session));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 触屏开关非0或1被拒绝()
    {
        EnsureDllAvailable();
        var request = NewRequest(Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe"), 2, 1);
        var result = new HoyoResult();
        try
        {
            Assert.Equal(HOYO_ERR_INVALID_ARGUMENT, hoyo_create_session(ref request, ref result, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 旧V2大小请求被接受且按触屏开启处理_FPS关闭也成功()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequestV2
        {
            Size = (uint)Marshal.SizeOf<HoyoLaunchRequestV2>(),
            AbiVersion = ABI_VERSION,
            GameType = 1,
            FpsUnlockEnabled = 0, // 旧调用方的「纯触屏」：缺省触屏载荷开启（不是 PC 界面 + 无 FPS）
            TargetFps = 120,
            GameExecutablePath = Marshal.StringToHGlobalUni(path),
            GameExecutablePathChars = (uint)path.Length,
        };
        var result = new HoyoResult();
        try
        {
            var rc = hoyo_create_session_v2(ref request, ref result, out var session);
            Assert.Equal(HOYO_OK, rc);
            Assert.Equal(HOYO_OK, hoyo_release(session));
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    [Fact]
    public void 小于V2的请求大小被拒绝()
    {
        EnsureDllAvailable();
        var path = Path.Combine(Path.GetTempPath(), "sparxie-abi-test", "StarRail.exe");
        var request = new HoyoLaunchRequestV2
        {
            Size = 80,
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
            Assert.Equal(HOYO_ERR_ABI_MISMATCH, hoyo_create_session_v2(ref request, ref result, out _));
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
        }
    }

    /// <summary>
    /// 开关确实抵达 Runtime 并改变行为：假星铁目录（真实可执行文件，无 GameAssembly.dll/UnityPlayer.dll）。
    /// 触屏载荷开启时星铁先取 Bootui（需要 GameAssembly.dll）→ 失败于触屏扫描阶段；
    /// PC 界面会话跳过 Bootui，直接进入引擎模块加载 → 失败于加载引擎阶段（不同阶段 = 开关生效）。
    /// </summary>
    [Theory]
    [InlineData(0, HOYO_STAGE_SCAN_TOUCH)]
    [InlineData(1, HOYO_STAGE_LOAD_ENGINE)]
    public void 星铁假目录_开关改变失败阶段(int touchUiDisabled, uint expectedStage)
    {
        EnsureDllAvailable();
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-abi-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "StarRail.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), exe);
        var request = NewRequest(exe, touchUiDisabled, 1);
        try
        {
            var result = new HoyoResult();
            Assert.Equal(HOYO_OK, hoyo_create_session(ref request, ref result, out var session));

            result = new HoyoResult();
            var rc = hoyo_launch(session, 0, ref result);
            Assert.NotEqual(HOYO_OK, rc);
            Assert.Equal(expectedStage, result.Stage);

            Assert.Equal(HOYO_OK, hoyo_release(session));
        }
        finally
        {
            Marshal.FreeHGlobal(request.GameExecutablePath);
            // bootstrap 终止的挂起进程可能还在退出，目录删除重试几次
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(dir, recursive: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
            }
        }
    }
}
