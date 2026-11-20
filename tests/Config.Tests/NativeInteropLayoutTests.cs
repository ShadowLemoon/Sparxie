using System.Runtime.InteropServices;
using Sparxie.Infrastructure.Plugins;

namespace Config.Tests;

/// <summary>
/// 不依赖 mock DLL 的互操作布局测试：字节数来自头文件在 x64/MSVC 下的结构体大小。
/// 这些测试在 CI 里一定会执行（native 插件测试依赖 mock 是否已构建）。
/// </summary>
public sealed class NativeInteropLayoutTests
{
    [Fact]
    public void 结构体大小与头文件一致()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(16, Marshal.SizeOf<NativePluginLibrary.SpxStr>());
        Assert.Equal(16 + (512 * 2), Marshal.SizeOf<NativePluginLibrary.SpxResult>());
        Assert.Equal(88, Marshal.SizeOf<NativePluginLibrary.SpxModuleInfo>());
        Assert.Equal(96, Marshal.SizeOf<NativePluginLibrary.SpxLaunchRequest>());
        Assert.Equal(48, Marshal.SizeOf<NativePluginLibrary.SpxHostApi>());
        Assert.Equal(96, Marshal.SizeOf(typeof(NativePluginLibrary).GetNestedType("ApiRaw", System.Reflection.BindingFlags.NonPublic)!));
    }

    [Fact]
    public void 关键字段偏移与头文件一致()
    {
        Assert.Equal(16, (int)Marshal.OffsetOf<NativePluginLibrary.SpxResult>(nameof(NativePluginLibrary.SpxResult.Message)));
        Assert.Equal(4, (int)Marshal.OffsetOf<NativePluginLibrary.SpxResult>(nameof(NativePluginLibrary.SpxResult.ErrorCode)));
        Assert.Equal(88, (int)Marshal.OffsetOf<NativePluginLibrary.SpxLaunchRequest>(nameof(NativePluginLibrary.SpxLaunchRequest.Host)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NativePluginLibrary.SpxHostApi>(nameof(NativePluginLibrary.SpxHostApi.Context)));
        Assert.Equal(24, (int)Marshal.OffsetOf<NativePluginLibrary.SpxHostApi>(nameof(NativePluginLibrary.SpxHostApi.RegistrySetDword)));
    }

    [Fact]
    public void 回调的字符串读取严格_超长或空指针不被截成空串()
    {
        var ptr = Marshal.StringToHGlobalUni("hello");
        try
        {
            Assert.True(NativePluginSession.TryRead(new NativePluginLibrary.SpxStr { P = ptr, Chars = 5 }, 10, out var text));
            Assert.Equal("hello", text);
            Assert.False(NativePluginSession.TryRead(new NativePluginLibrary.SpxStr { P = ptr, Chars = 5 }, 4, out _)); // 超长
            Assert.False(NativePluginSession.TryRead(new NativePluginLibrary.SpxStr { P = IntPtr.Zero, Chars = 3 }, 10, out _)); // 空指针
            Assert.True(NativePluginSession.TryRead(default, 10, out var empty));
            Assert.Equal(string.Empty, empty);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void 回调外壳对NULL_result与异常都安全()
    {
        Assert.Equal(0, NativePluginSession.Guard(IntPtr.Zero, () => (true, string.Empty)));
        Assert.Equal(1, NativePluginSession.Guard(IntPtr.Zero, () => (false, "denied"))); // result 为 NULL 也不崩
        Assert.Equal(10, NativePluginSession.Guard(IntPtr.Zero, () => throw new InvalidOperationException("boom")));

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativePluginLibrary.SpxResult>());
        try
        {
            Marshal.StructureToPtr(NativePluginLibrary.SpxResult.New(), buffer, fDeleteOld: false);
            Assert.Equal(10, NativePluginSession.Guard(buffer, () => throw new ArgumentException("bad")));
            var result = Marshal.PtrToStructure<NativePluginLibrary.SpxResult>(buffer);
            Assert.Equal(10, result.ErrorCode);
            Assert.Contains("bad", result.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void size不够时不写消息缓冲()
    {
        var total = Marshal.SizeOf<NativePluginLibrary.SpxResult>();
        var buffer = Marshal.AllocHGlobal(total);
        try
        {
            for (var i = 0; i < total; i += 4)
            {
                Marshal.WriteInt32(buffer, i, 0x5A5A5A5A);
            }

            Marshal.WriteInt32(buffer, 0, 8); // size 只覆盖到 error_code 之前
            NativePluginSession.Guard(buffer, () => (false, "x"));

            Assert.Equal(0x5A5A5A5A, Marshal.ReadInt32(buffer, 16)); // 消息缓冲原样未动
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
