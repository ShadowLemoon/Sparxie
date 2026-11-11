using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>游戏注册表访问（相对 HKCU）。抽象出来便于单元测试，不碰真实的游戏设置。</summary>
public interface IGameRegistry
{
    bool KeyExists(string subKey);

    int? GetDword(string subKey, string name);

    byte[]? GetBytes(string subKey, string name);

    /// <summary>直接子键名；键不存在返回空。</summary>
    IReadOnlyList<string> GetSubKeyNames(string subKey);

    string? GetString(string subKey, string name);

    void SetDword(string subKey, string name, int value);

    void SetBytes(string subKey, string name, byte[] value);
}

/// <summary>HKCU 下的真实实现；<paramref name="root"/> 只在测试里用来指向临时键。</summary>
public sealed class CurrentUserGameRegistry : IGameRegistry
{
    private readonly string _root;

    public CurrentUserGameRegistry(string root = "")
    {
        _root = root;
    }

    private string Full(string subKey) => _root.Length == 0 ? subKey : _root + "\\" + subKey;

    public bool KeyExists(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Full(subKey));
        return key is not null;
    }

    public int? GetDword(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Full(subKey));
        return key?.GetValue(name) is int value ? value : null;
    }

    public byte[]? GetBytes(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Full(subKey));
        return key?.GetValue(name) as byte[];
    }

    public IReadOnlyList<string> GetSubKeyNames(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Full(subKey));
        return key?.GetSubKeyNames() ?? [];
    }

    public string? GetString(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Full(subKey));
        return key?.GetValue(name) as string;
    }

    public void SetDword(string subKey, string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Full(subKey));
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void SetBytes(string subKey, string name, byte[] value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Full(subKey));
        key.SetValue(name, value, RegistryValueKind.Binary);
    }
}

/// <summary>一次写入的结果：写了哪些键、跳过了什么，供日志 / 调试提示使用。</summary>
public sealed record GameRegistryResult(IReadOnlyList<string> WrittenKeys, string? Note)
{
    public bool Applied => WrittenKeys.Count > 0;
}

/// <summary>
/// 启动前把分辨率 / 全屏写入游戏自己的注册表设置（做法参考 Starward 的 GameSettingService）。
/// 只写已存在的游戏键（游戏从未运行过时没有这个键，跳过并说明，不凭空创建）。
/// 星铁 / 绝区零国服与国际服的 EXE 同名，无法按 EXE 区分，存在几个键就都写。
/// </summary>
public static class GameRegistrySettings
{
    public const string FullscreenMode = "Screenmanager Fullscreen mode_h3630240806"; // 1 全屏，3 窗口
    public const string IsFullscreen = "Screenmanager Is Fullscreen mode_h3981298716"; // 原神：0/1
    public const string ResolutionWidth = "Screenmanager Resolution Width_h182942802";
    public const string ResolutionHeight = "Screenmanager Resolution Height_h2627697771";
    public const string StarRailResolution = "GraphicsSettings_PCResolution_h431323223"; // JSON（UTF-8，结尾 \0）

    private const string GenshinCn = @"Software\miHoYo\原神";
    private const string GenshinGlobal = @"Software\miHoYo\Genshin Impact";
    private const string StarRailCn = @"Software\miHoYo\崩坏：星穹铁道";
    private const string StarRailGlobal = @"Software\Cognosphere\Star Rail";
    private const string ZzzCn = @"Software\miHoYo\绝区零";
    private const string ZzzGlobal = @"Software\miHoYo\ZenlessZoneZero";

    /// <summary>
    /// 这份 EXE 对应的游戏注册表键。原神靠 EXE 名区分国服 / 国际服；星铁、绝区零 EXE 同名，
    /// 先像 Starward 那样现场读取米哈游启动器登记的游戏安装路径来认服务器，认不出（没有登记，如手动解压的安装）时两个键都候选。
    /// </summary>
    public static IReadOnlyList<string> CandidateKeys(GameType game, string executablePath, IGameRegistry? registry = null)
    {
        var exe = Path.GetFileName(executablePath);
        if (game == GameType.Genshin)
        {
            return [string.Equals(exe, "GenshinImpact.exe", StringComparison.OrdinalIgnoreCase) ? GenshinGlobal : GenshinCn];
        }

        var (cn, global) = game == GameType.StarRail ? (StarRailCn, StarRailGlobal) : (ZzzCn, ZzzGlobal);
        return InstalledServerLocator.Resolve(game, executablePath, registry) switch
        {
            GameServer.Cn => [cn],
            GameServer.Global => [global],
            _ => [cn, global],
        };
    }

    public static GameRegistryResult Apply(GameType game, string executablePath, LaunchSettings? launch, IGameRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (launch is null || (launch.Width is null && launch.Height is null && launch.Fullscreen is null))
        {
            return new GameRegistryResult([], null);
        }

        var written = new List<string>();
        foreach (var key in CandidateKeys(game, executablePath, registry))
        {
            if (!registry.KeyExists(key))
            {
                continue;
            }

            WriteOne(game, key, launch, registry);
            written.Add(key);
        }

        return new GameRegistryResult(
            written,
            written.Count == 0 ? "游戏还没有运行过（没有找到它的注册表设置），分辨率 / 窗口模式本次未写入" : null);
    }

    private static void WriteOne(GameType game, string key, LaunchSettings launch, IGameRegistry registry)
    {
        var fullscreen = launch.Fullscreen;

        if (launch.Width is { } width && launch.Height is { } height)
        {
            registry.SetDword(key, ResolutionWidth, width);
            registry.SetDword(key, ResolutionHeight, height);
        }

        if (fullscreen is { } full)
        {
            if (game == GameType.Genshin)
            {
                registry.SetDword(key, IsFullscreen, full ? 1 : 0);
            }
            else
            {
                registry.SetDword(key, FullscreenMode, full ? 1 : 3);
            }
        }

        if (game == GameType.StarRail)
        {
            WriteStarRailResolutionJson(key, launch, fullscreen, registry);
        }
    }

    /// <summary>星铁另有一份 JSON：只改我们要改的字段，其余沿用游戏已有的值；没有已有值又缺宽高时不写。</summary>
    private static void WriteStarRailResolutionJson(string key, LaunchSettings launch, bool? fullscreen, IGameRegistry registry)
    {
        JsonObject node;
        var existing = registry.GetBytes(key, StarRailResolution);
        try
        {
            node = existing is null
                ? new JsonObject()
                : JsonNode.Parse(Encoding.UTF8.GetString(existing).TrimEnd('\0')) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            node = new JsonObject();
        }

        if (launch.Width is { } width && launch.Height is { } height)
        {
            node["width"] = width;
            node["height"] = height;
        }

        if (fullscreen is { } full)
        {
            node["isFullScreen"] = full;
        }

        if (node["width"] is null || node["height"] is null)
        {
            return; // 缺宽高时写出的 JSON 游戏读不了，宁可不写
        }

        registry.SetBytes(key, StarRailResolution, Encoding.UTF8.GetBytes(node.ToJsonString() + "\0"));
    }
}

public enum GameServer
{
    /// <summary>认不出（启动器没有登记这个安装）。</summary>
    Unknown,

    /// <summary>国服（含 B 服）。</summary>
    Cn,

    /// <summary>国际服。</summary>
    Global,
}

/// <summary>
/// 现场读取米哈游启动器（HYP）在 HKCU 里登记的各游戏安装路径（<c>GameInstallPath</c>），
/// 看这份 EXE 在哪个服务器的安装目录下。做法同 Starward（它按服务器标识读注册表里的路径）。
/// </summary>
public static class InstalledServerLocator
{
    private static readonly string[] HypRoots = [@"Software\miHoYo\HYP", @"Software\Cognosphere\HYP"];

    public static GameServer Resolve(GameType game, string executablePath, IGameRegistry? registry)
    {
        if (registry is null || string.IsNullOrWhiteSpace(executablePath))
        {
            return GameServer.Unknown;
        }

        var prefix = game switch
        {
            GameType.Genshin => "hk4e_",
            GameType.StarRail => "hkrpg_",
            _ => "nap_",
        };
        var exeDirectory = Normalize(Path.GetDirectoryName(executablePath));
        var matched = new HashSet<GameServer>();
        foreach (var root in HypRoots)
        {
            Walk(registry, root, depth: 0, key =>
            {
                var name = key[(key.LastIndexOf('\\') + 1)..];
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var installPath = Normalize(registry.GetString(key, "GameInstallPath"));
                if (installPath.Length > 0 && IsSameOrUnder(exeDirectory, installPath))
                {
                    matched.Add(name.EndsWith("global", StringComparison.OrdinalIgnoreCase) ? GameServer.Global : GameServer.Cn);
                }
            });
        }

        // 只有恰好认出一个服务器才采用；两个都命中（极少见）或都没命中都回到“都候选”。
        return matched.Count == 1 ? matched.Single() : GameServer.Unknown;
    }

    private static void Walk(IGameRegistry registry, string key, int depth, Action<string> visit)
    {
        if (depth > 6)
        {
            return;
        }

        foreach (var child in registry.GetSubKeyNames(key))
        {
            var childKey = key + "\\" + child;
            visit(childKey);
            Walk(registry, childKey, depth + 1, visit);
        }
    }

    private static string Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\').TrimEnd('\\');

    private static bool IsSameOrUnder(string directory, string root) =>
        directory.Equals(root, StringComparison.OrdinalIgnoreCase)
        || directory.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
