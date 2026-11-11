using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>由启动设置生成传给游戏进程的命令行参数。分辨率和全屏走注册表，这里只负责无边框和附加参数。</summary>
public static class LaunchArguments
{
    /// <summary>Unity 的无边框窗口参数。</summary>
    public const string Borderless = "-popupwindow";

    public static string Build(bool borderless, string? extraArguments)
    {
        var parts = new List<string>();
        var extra = extraArguments?.Trim() ?? string.Empty;

        // 无边框：自动注入，用户在附加参数里已经写了就不重复。
        if (borderless
            && !extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(Borderless, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(Borderless);
        }

        if (extra.Length > 0)
        {
            parts.Add(extra);
        }

        return string.Join(' ', parts);
    }

    public static string Build(LaunchSettings? launch) => Build(launch?.Borderless ?? false, launch?.ExtraArguments);
}
