using System.Runtime.InteropServices;

namespace SessionHost.Tests;

public sealed class EndfieldFakeGameTests
{
    [Fact]
    public void FakeGameReachesEndfieldScanAndFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparxie-endfield-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fakeGame = Path.Combine(root, "Endfield.exe");
        File.Copy(Environment.ProcessPath!, fakeGame);
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
        };
        var result = new Result { Size = (uint)Marshal.SizeOf<Result>() };
        try
        {
            Assert.Equal(0, endfield_create_session(ref request, ref result, out var session));
            try
            {
                Assert.Equal(4, endfield_launch(session, ref result));
                Assert.Equal(2u, result.Stage);  // no GameAssembly.dll beside the stand-in: the scan stage fails closed and the stand-in game is terminated
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
