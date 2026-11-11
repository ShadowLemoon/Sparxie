using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sparxie.Infrastructure.Processes;

/// <summary>
/// 绝区零 Steam 版的启动：游戏目录里有 <c>steam_appid.txt</c> 就认为是 Steam 安装，
/// 不直接运行 EXE，而是通过 <c>steam://run/&lt;AppID&gt;//&lt;参数&gt;</c> 让 Steam 拉起，再等游戏进程出现（做法参照 ZZZTouchLauncher）。
/// </summary>
public static partial class SteamLaunch
{
    public const string AppIdFileName = "steam_appid.txt";

    /// <summary>绝区零在 Steam 上的 AppID。</summary>
    public const string ZenlessZoneZeroAppId = "4162040";

    /// <summary>EXE 所在目录有 steam_appid.txt。</summary>
    public static bool IsSteamInstall(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        return !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, AppIdFileName));
    }

    /// <summary>生成 Steam 启动 URI；游戏参数整体做 URI 转义后跟在 <c>//</c> 后面。</summary>
    public static string BuildUri(string appId, string arguments) =>
        $"steam://run/{appId}//{Uri.EscapeDataString(arguments ?? string.Empty)}";

    /// <summary>
    /// 请求 Steam 启动游戏并等待目标进程出现。进程必须是请求之后新出现的、且 EXE 路径与配置一致（读不到路径时放宽为按进程名匹配）。
    /// 一直等到进程出现、<paramref name="timeout"/> 到期或取消。
    /// </summary>
    public static async Task<Process> StartAsync(
        string appId,
        string executablePath,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        var before = Process.GetProcessesByName(processName).Select(process => process.Id).ToHashSet();
        var logPath = FindLaunchLogPath();
        var logOffset = LogLength(logPath);

        using (Process.Start(new ProcessStartInfo { FileName = BuildUri(appId, arguments), UseShellExecute = true }))
        {
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (!before.Contains(process.Id) && PathMatches(process, executablePath))
                {
                    return process;
                }

                process.Dispose();
            }

            // Steam 启动失败（没有许可、区服不对、需要更新…）不会产生游戏进程，只会在 Steam 日志里留一行 launch error，
            // 不检查的话会一直傻等到超时。
            if (TryParseLaunchError(ReadLogSince(logPath, logOffset), appId, out var launchError))
            {
                throw new InvalidOperationException(launchError);
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"等待 Steam 拉起游戏超时（{timeout.TotalMinutes:0} 分钟）。请确认 Steam 已登录、没有卡在更新或确认弹窗，且与本程序使用相同权限级别。");
    }

    private static bool PathMatches(Process process, string executablePath)
    {
        try
        {
            var actual = process.MainModule?.FileName;
            return actual is null || string.Equals(Path.GetFullPath(actual), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读不到路径（刚创建、权限隔离）：不排除，按进程名匹配
            return true;
        }
    }

    /// <summary>Steam 的 webhelper_js.txt：启动失败会记一行 “LaunchGameAction: launch error 1 &lt;AppID&gt; LaunchApp AppError_5”。</summary>
    public static string? FindLaunchLogPath()
    {
        try
        {
            var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(steamPath) ? null : Path.Combine(steamPath.Replace('/', '\\'), "logs", "webhelper_js.txt");
        }
        catch
        {
            return null;
        }
    }

    private static long LogLength(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadLogSince(string? path, long offset)
    {
        if (path is null)
        {
            return string.Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset)
            {
                offset = 0; // 日志被轮转
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>在启动请求之后新增的日志里找这个 AppID 的启动失败记录，并翻译成给用户看的话。</summary>
    public static bool TryParseLaunchError(string logText, string appId, out string message)
    {
        message = string.Empty;
        foreach (Match match in LaunchErrorPattern().Matches(logText))
        {
            if (match.Groups[1].Value != appId)
            {
                continue;
            }

            var code = match.Groups[2].Value;
            message = code switch
            {
                "5" => "Steam 启动失败：当前 Steam 账号没有这款游戏的许可（AppError_5）。请确认登录的是购买了绝区零的账号、区服也对得上。",
                _ => $"Steam 启动失败（AppError_{code}）。请在 Steam 里看看具体提示。",
            };
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"launch error \d+ (\d+) LaunchApp AppError_(\d+)")]
    private static partial Regex LaunchErrorPattern();
}
