using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_get_abi_version(out uint version, out uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Req
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
        public IntPtr Path;
        public uint PathChars;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Res
    {
        public uint Size;
        public int ErrorCode;
        public uint Stage;
        public uint Detail;
        public IntPtr Message;
        public uint MessageChars;
    }

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_create_session(ref Req request, ref Res result, out IntPtr session);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_launch(IntPtr session, uint gamePid, ref Res result);

    [DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hoyo_release(IntPtr session);

    private static int Main()
    {
        Console.WriteLine($"[repro-console] pid={Environment.ProcessId} serverGC={System.Runtime.GCSettings.IsServerGC}");
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("DOTNET") || k.StartsWith("COMPlus") || k.StartsWith("VSTEST") || k.StartsWith("COREHOST")).OrderBy(k => k))
        {
            Console.WriteLine($"[env] {key}={Environment.GetEnvironmentVariable(key)}");
        }
        var rc = hoyo_get_abi_version(out var ver, out var size);
        Console.WriteLine($"[repro-console] abi rc={rc} version={ver} size={size}");

        // 无真实游戏：用不存在的路径触发 bootstrap（init_API → 扫描失败路径）
        var fake = @"D:\Code\Sparxie\.pai-temp-repro\no-such-game\StarRail.exe";
        var request = new Req
        {
            Size = (uint)Marshal.SizeOf<Req>(),
            AbiVersion = 1,
            GameType = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 120,
            BackgroundFpsLimitEnabled = 0,
            BackgroundFps = 10,
            ProcessPriority = 1,
            Path = Marshal.StringToHGlobalUni(fake),
            PathChars = (uint)fake.Length,
        };
        var result = new Res();
        try
        {
            var rc2 = hoyo_create_session(ref request, ref result, out var session);
            Console.WriteLine($"[repro-console] create rc={rc2}");
            result = new Res { Size = (uint)Marshal.SizeOf<Res>() };
            var rc3 = hoyo_launch(session, 0, ref result);
            Console.WriteLine($"[repro-console] launch rc={rc3} stage={result.Stage} detail={result.Detail} msg={Marshal.PtrToStringUni(result.Message)}");
            hoyo_release(session);
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(request.Path);
        }
    }
}
