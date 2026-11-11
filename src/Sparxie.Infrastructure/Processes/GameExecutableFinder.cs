using Microsoft.Win32;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>
/// 找到的一个候选游戏 EXE：完整路径、来源说明，以及优先级（越小越优先：国服 0 &lt; B 服 1 &lt; 认不出服务器 2 &lt; 国际服 3）。
/// </summary>
public sealed record GameExecutableCandidate(string Path, string Source, int Priority = 2);

/// <summary>查找所需的系统信息，抽象出来便于单元测试（不碰真实注册表 / 进程 / 磁盘）。</summary>
public interface IGameFinderEnvironment
{
    /// <summary>米哈游启动器（HYP）在 HKCU 登记的游戏安装目录（<c>GameInstallPath</c>），带服务器标识（如 nap_global）。</summary>
    IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories();

    /// <summary>HKLM / HKCU 卸载项里的启动器 / 旧版游戏安装目录（<c>InstallPath</c> / <c>InstallLocation</c>），带键名。</summary>
    IEnumerable<(string Name, string Directory)> UninstallInstallDirectories();

    /// <summary>Windows 记录的“运行过的程序”路径（MuiCache、兼容性助手记录）：手动解压 / 没有启动器登记的安装靠它找回。</summary>
    IEnumerable<string> RecentExecutablePaths();

    /// <summary>正在运行的同名进程的 EXE 完整路径（读不到的进程跳过）。</summary>
    IEnumerable<string> RunningExecutablePaths(string executableName);

    bool FileExists(string path);

    /// <summary>在目录下按深度上限查找指定文件名（只用来在启动器安装目录里找游戏子目录）。</summary>
    IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth);
}

/// <summary>
/// 自动查找游戏 EXE：现场读取米哈游启动器登记的安装路径（同 Starward），再看卸载项里的安装目录、
/// 正在运行的游戏进程。只返回文件存在且文件名在该游戏白名单里的候选。
/// </summary>
public static class GameExecutableFinder
{
    /// <summary>从候选里挑出要填的那一个：跳过已经被现有配置用掉的路径，剩下的按优先级（国服优先）取第一个；没有返回 null。</summary>
    public static GameExecutableCandidate? PickUnused(
        IEnumerable<GameExecutableCandidate> candidates, IEnumerable<string?> usedPaths)
    {
        var used = usedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('/', '\\').TrimEnd('\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(candidate => !used.Contains(candidate.Path.TrimEnd('\\')))
            .OrderBy(candidate => candidate.Priority)
            .FirstOrDefault();
    }

    public static IReadOnlyList<GameExecutableCandidate> Find(GameType game, IGameFinderEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var names = GameExecutables.For(game);
        var prefix = game switch
        {
            GameType.Genshin => "hk4e_",
            GameType.StarRail => "hkrpg_",
            _ => "nap_",
        };
        var found = new List<GameExecutableCandidate>();

        void Add(string path, string source, int priority)
        {
            var normalized = path.Replace('/', '\\');
            // 只收本机盘符路径：最近运行记录里混有 /home/... 这类别的环境留下的路径，不是 Windows 上能直接用的。
            if (normalized.Length > 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'
                && GameExecutables.IsAllowed(game, normalized)
                && environment.FileExists(normalized)
                && !found.Any(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new GameExecutableCandidate(normalized, source, priority));
            }
        }

        // 1) 启动器登记的游戏安装目录（直接就是 EXE 所在目录）
        foreach (var (biz, directory) in environment.HypGameInstallDirectories())
        {
            if (!biz.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var name in names)
            {
                Add(System.IO.Path.Combine(directory.Replace('/', '\\'), name), $"米哈游启动器登记（{ServerName(biz)}）", PriorityOf(biz));
            }
        }

        // 2) 卸载项里的启动器 / 旧版安装目录：游戏在其下的子目录里（如 games\ZenlessZoneZero Game、Genshin Impact Game）
        foreach (var (key, directory) in environment.UninstallInstallDirectories())
        {
            if (!MentionsGame(game, key))
            {
                continue;
            }

            foreach (var name in names)
            {
                foreach (var file in environment.FindFiles(directory.Replace('/', '\\'), name, maxDepth: 3))
                {
                    Add(file, "启动器安装目录", PriorityOf(key));
                }
            }
        }

        // 3) Windows 记录的运行过的程序（MuiCache / 兼容性助手）：服务器只能靠 EXE 名判断（原神国服 / 国际服 EXE 名不同，其余认不出）
        foreach (var path in environment.RecentExecutablePaths())
        {
            var exeName = System.IO.Path.GetFileName(path);
            var priority = string.Equals(exeName, "YuanShen.exe", StringComparison.OrdinalIgnoreCase) ? 0
                : string.Equals(exeName, "GenshinImpact.exe", StringComparison.OrdinalIgnoreCase) ? 3
                : PriorityOf(System.IO.Path.GetDirectoryName(path) ?? string.Empty); // 星铁 / 绝区零同名：看目录名里的 _cn / global 等提示，没有提示就是 2
            Add(path, "最近运行过的程序记录", priority);
        }

        // 4) 正在运行的游戏进程
        foreach (var name in names)
        {
            foreach (var path in environment.RunningExecutablePaths(System.IO.Path.GetFileNameWithoutExtension(name)))
            {
                Add(path, "正在运行的游戏", 2);
            }
        }

        return found.OrderBy(item => item.Priority).ToList();
    }

    /// <summary>按键名 / 服务器标识推断优先级：国服 0，B 服 1，认不出 2，国际服 3。</summary>
    public static int PriorityOf(string name)
    {
        if (name.Contains("global", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (name.Contains("bilibili", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return name.Contains("_cn", StringComparison.OrdinalIgnoreCase) || name.Any(c => c >= '\u4e00' && c <= '\u9fff') ? 0 : 2;
    }

    private static string ServerName(string biz) =>
        biz.EndsWith("global", StringComparison.OrdinalIgnoreCase) ? "国际服"
        : biz.EndsWith("bilibili", StringComparison.OrdinalIgnoreCase) ? "B 服"
        : "国服";

    /// <summary>卸载项键名是否属于这款游戏（HYP_…_hkrpg_cn_xxx、星穹铁道、Star Rail、ZenlessZoneZero、绝区零、原神、Genshin…）。</summary>
    private static bool MentionsGame(GameType game, string key)
    {
        string[] hints = game switch
        {
            GameType.Genshin => ["hk4e", "原神", "Genshin"],
            GameType.StarRail => ["hkrpg", "星穹铁道", "Star Rail", "StarRail"],
            _ => ["nap_", "绝区零", "Zenless"],
        };
        return hints.Any(hint => key.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>真实系统实现：注册表（HKCU / HKLM 卸载项）、进程、磁盘。所有读取失败都当作“没有”。</summary>
public sealed class SystemGameFinderEnvironment : IGameFinderEnvironment
{
    private static readonly string[] HypRoots = [@"Software\miHoYo\HYP", @"Software\Cognosphere\HYP"];

    public IEnumerable<(string Biz, string Directory)> HypGameInstallDirectories()
    {
        var results = new List<(string, string)>();
        foreach (var root in HypRoots)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(root);
                if (key is not null)
                {
                    Walk(key, depth: 0, results);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 读不到就当没有
            }
        }

        return results;
    }

    private static void Walk(RegistryKey key, int depth, List<(string, string)> results)
    {
        if (depth > 6)
        {
            return;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is null)
            {
                continue;
            }

            if (child.GetValue("GameInstallPath") is string path && path.Length > 0)
            {
                results.Add((name, path));
            }

            Walk(child, depth + 1, results);
        }
    }

    public IEnumerable<(string Name, string Directory)> UninstallInstallDirectories()
    {
        var results = new List<(string, string)>();
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var child = uninstall.OpenSubKey(name);
                    var directory = child?.GetValue("InstallPath") as string ?? child?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        results.Add((name, directory));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 读不到就当没有
            }
        }

        return results;
    }

    public IEnumerable<string> RecentExecutablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // MuiCache：值名是“完整路径.FriendlyAppName / .ApplicationCompany”
            using var mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
            foreach (var name in mui?.GetValueNames() ?? [])
            {
                var dot = name.LastIndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
                if (dot > 0)
                {
                    paths.Add(name[..(dot + 4)]);
                }
            }

            // 兼容性助手记录：值名就是完整路径
            using var store = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store");
            foreach (var name in store?.GetValueNames() ?? [])
            {
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读不到就当没有
        }

        return paths;
    }

    public IEnumerable<string> RunningExecutablePaths(string executableName)
    {
        var paths = new List<string>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(executableName))
        {
            try
            {
                if (process.MainModule?.FileName is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // 提权进程读不到路径：跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        return paths;
    }

    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> FindFiles(string directory, string fileName, int maxDepth)
    {
        var results = new List<string>();
        Search(directory, fileName, maxDepth, results);
        return results;
    }

    private static void Search(string directory, string fileName, int depth, List<string> results)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var file = System.IO.Path.Combine(directory, fileName);
            if (File.Exists(file))
            {
                results.Add(file);
            }

            if (depth <= 0)
            {
                return;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                Search(child, fileName, depth - 1, results);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 无权限的目录跳过
        }
    }
}
