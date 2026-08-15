using System.Diagnostics;
using System.Runtime.InteropServices;

// 查询当前进程的 CET shadow stack 与强制策略
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMitigationShadowStackPolicy
{
    public uint EnableShadowStack;
    public uint BlockNonCetBinaries;
    public uint BlockNonCetBinariesNonEhcont;
    public uint DisableShadowStack;
    public uint AuditShadowStack;
    public uint SetShadowStack;
    public uint EnableShadowStackWithoutThunk;
    public uint AuditShadowStackWithoutThunk;
}

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessMitigationPolicy(
        IntPtr hProcess, int policy, out ProcessMitigationShadowStackPolicy lpBuffer, nuint size);

    private const int ProcessShadowStackPolicy = 15; // ProcessUserShadowStackPolicy

    private static int Main()
    {
        Console.WriteLine($"[cet-probe] pid={Environment.ProcessId} arch={RuntimeInformation.ProcessArchitecture}");
        var ok = GetProcessMitigationPolicy(
            Process.GetCurrentProcess().Handle,
            ProcessShadowStackPolicy,
            out var policy,
            (nuint)Marshal.SizeOf<ProcessMitigationShadowStackPolicy>());
        if (!ok)
        {
            Console.WriteLine($"[cet-probe] GetProcessMitigationPolicy failed: {Marshal.GetLastWin32Error()}");
            return -1;
        }

        Console.WriteLine($"[cet-probe] EnableShadowStack={policy.EnableShadowStack}");
        Console.WriteLine($"[cet-probe] BlockNonCetBinaries={policy.BlockNonCetBinaries}");
        Console.WriteLine($"[cet-probe] DisableShadowStack={policy.DisableShadowStack}");
        Console.WriteLine($"[cet-probe] SetShadowStack={policy.SetShadowStack}");
        return 0;
    }
}
