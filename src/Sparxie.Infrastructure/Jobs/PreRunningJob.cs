using System.Runtime.InteropServices;

namespace Sparxie.Infrastructure.Jobs;

/// <summary>
/// Running 前失效保护 Job：句柄关闭即终止其中所有进程。
/// 进入 Running 时通过 ReleaseKillOnClose 可验证地清除 KILL_ON_JOB_CLOSE 语义，
/// 之后句柄关闭不再终止游戏进程。
/// </summary>
public sealed class PreRunningJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private SafeJobHandle? _handle;
    private bool _killOnCloseActive = true;

    /// <summary>底层 Job 句柄（供 Hoyo bootstrap Assign 游戏进程）。</summary>
    public IntPtr JobHandle => _handle?.DangerousGetHandle() ?? IntPtr.Zero;

    private PreRunningJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static PreRunningJob Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"CreateJobObjectW 失败: {Marshal.GetLastWin32Error()}");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                throw new InvalidOperationException($"SetInformationJobObject 失败: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new PreRunningJob(handle);
    }

    public void Assign(SafeHandle processHandle)
    {
        if (_handle is null)
        {
            throw new ObjectDisposedException(nameof(PreRunningJob));
        }

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new InvalidOperationException($"AssignProcessToJobObject 失败: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>进入 Running 前撤销杀进程语义，并读回确认标志已清除。</summary>
    public void ReleaseKillOnClose()
    {
        if (_handle is null)
        {
            throw new ObjectDisposedException(nameof(PreRunningJob));
        }

        if (!_killOnCloseActive)
        {
            return;
        }

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // 读回当前限制，仅清除 KILL_ON_JOB_CLOSE 位，保留其他标志。
            if (!QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size, out _))
            {
                throw new InvalidOperationException($"QueryInformationJobObject 失败: {Marshal.GetLastWin32Error()}");
            }

            var info = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
            info.BasicLimitInformation.LimitFlags &= ~JobObjectLimitKillOnJobClose;

            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                throw new InvalidOperationException($"撤销 KILL_ON_JOB_CLOSE 失败: {Marshal.GetLastWin32Error()}");
            }

            // 读回验证
            if (!QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size, out _))
            {
                throw new InvalidOperationException($"撤销后读回验证失败: {Marshal.GetLastWin32Error()}");
            }

            info = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
            if ((info.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) != 0)
            {
                throw new InvalidOperationException("撤销 KILL_ON_JOB_CLOSE 验证失败：标志仍存在");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _killOnCloseActive = false;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, SafeHandle hProcess);
}
