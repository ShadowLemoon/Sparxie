using System.Diagnostics;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>按进程名检测对应游戏是否已运行。同款游戏共享一个游戏级互斥域。</summary>
public static class GameProcessDetector
{
    /// <summary>该游戏当前的进程对象（调用方负责 Dispose）；用于订阅退出事件。</summary>
    public static Process[] FindRunning(GameType game)
    {
        var found = new List<Process>();
        foreach (var exe in GameExecutables.For(game))
        {
            found.AddRange(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)));
        }

        return found.ToArray();
    }

    /// <summary>按 EXE 名（可含 .exe）找正在运行的进程对象，调用方负责 Dispose；第三方插件游戏用 manifest 里的名字。</summary>
    public static Process[] FindRunning(IEnumerable<string> exeNames)
    {
        var found = new List<Process>();
        foreach (var exe in exeNames)
        {
            found.AddRange(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)));
        }

        return found.ToArray();
    }

    public static bool IsAnyRunning(IEnumerable<string> exeNames)
    {
        var names = exeNames.Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Count > 0 && names.Any(name => Process.GetProcessesByName(name).Length > 0);
    }

    public static bool IsGameRunning(string? gameId) =>
        GameIds.TryGetBuiltIn(gameId, out var game) && IsGameRunning(game);

    public static bool IsGameRunning(GameType game)
    {
        var names = GameExecutables.For(game)
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (names.Contains(process.ProcessName))
                {
                    return true;
                }
            }
            catch
            {
                // 进程已退出或无权访问，跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
