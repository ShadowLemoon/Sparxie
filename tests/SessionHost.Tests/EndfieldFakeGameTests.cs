using System.Runtime.InteropServices;

namespace SessionHost.Tests;

public sealed class EndfieldFakeGameTests
{
    // ENDFIELD_FLAG_TEST_SKIP_SIGNER_CHECK: the stand-in game and decoy source are not Authenticode-signed.
    private const uint SkipSignerCheck = 1;

    [Fact]
    public void FakeGameReachesEndfieldScanAndFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparxie-endfield-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fakeGame = Path.Combine(root, "Endfield.exe");
        File.Copy(Environment.ProcessPath!, fakeGame);
        // the decoy parent's source image, found in the game's folder as the Collapse plugin does
        File.Copy(Environment.ProcessPath!, Path.Combine(root, "PlatformProcess.exe"));
        var path = Marshal.StringToHGlobalUni(fakeGame);
        var request = new LaunchRequest
        {
            Size = (uint)Marshal.SizeOf<LaunchRequest>(),
            AbiVersion = 1,
            FpsUnlockEnabled = 1,
            TargetFps = 240,
            TouchUiEnabled = 1,
            Width = 1920,
            Height = 1080,
            RefreshRate = 120,
            GameExecutablePath = path,
            GameExecutablePathChars = (uint)fakeGame.Length,
            Flags = SkipSignerCheck,
        };
        var result = new Result { Size = (uint)Marshal.SizeOf<Result>() };
        try
        {
            Assert.Equal(0, endfield_create_session(ref request, ref result, out var session));
            try
            {
                Assert.Equal(4, endfield_launch(session, ref result));
                Assert.Equal(2u, result.Stage);

                // the scan failed after the game and its decoy parent were created: both are gone and the session folder is removed
                var info = new SessionInfo { Size = (uint)Marshal.SizeOf<SessionInfo>() };
                Assert.Equal(0, endfield_get_session_info(session, ref info));
                Assert.Equal(0u, info.DecoyPid);
                Assert.NotEqual(0u, info.GamePid);
                Assert.True(WaitGone(info.GamePid));
                var stageRoot = Path.Combine(root, "_Hi3_Hg", "parent");
                Assert.False(Directory.Exists(stageRoot) && Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories).Any());
            }
            finally
            {
                endfield_release(session);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(path);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool WaitGone(uint pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public uint Size;
        public uint GamePid;
        public uint DecoyPid;
        public uint ParentPidSeen;
        public IntPtr DecoyImage;
        public uint DecoyImageChars;
    }

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_get_session_info(IntPtr session, ref SessionInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LaunchRequest
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
        public uint Flags;
        public uint ParentSourcePathChars;
        public IntPtr ParentSourcePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public uint Size;
        public int ErrorCode;
        public uint Stage;
        public uint Detail;
        public IntPtr Message;
        public uint MessageChars;
    }

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_create_session(ref LaunchRequest request, ref Result result, out IntPtr session);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_launch(IntPtr session, ref Result result);

    [DllImport("EndfieldCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int endfield_release(IntPtr session);
}
