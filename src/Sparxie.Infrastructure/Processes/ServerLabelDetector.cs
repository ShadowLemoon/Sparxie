using System.Text.RegularExpressions;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>
/// 根据安装目录推断区服名称（国服 / 国际服 / B 服）。依据按可靠程度依次是：
/// 0) EXE 目录里有 steam_appid.txt = Steam；
/// 1) EXE 同级或上一级目录的 config.ini（官方启动器写入：game_biz=hk4e_cn、cps=bilibili、channel=14）；
/// 2) 米哈游启动器（HYP）登记的安装路径；
/// 3) 原神 EXE 名（YuanShen = 国服，GenshinImpact = 国际服）与目录名里的 global / bilibili / _cn 提示。
/// 认不出返回 null。
/// </summary>
public static partial class ServerLabelDetector
{
    public const string Cn = "国服";
    public const string Global = "国际服";
    public const string Bilibili = "B 服";
    public const string Steam = "Steam";

    public static string? Detect(GameType game, string executablePath, IGameRegistry? registry = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        // Steam 安装（目录里有 steam_appid.txt）优先，它的启动方式也和其他区服不同。
        if (SteamLaunch.IsSteamInstall(executablePath))
        {
            return Steam;
        }

        var directory = Path.GetDirectoryName(executablePath);
        for (var depth = 0; depth < 2 && !string.IsNullOrEmpty(directory); depth++, directory = Path.GetDirectoryName(directory))
        {
            if (FromConfigIni(Path.Combine(directory, "config.ini")) is { } fromIni)
            {
                return fromIni;
            }
        }

        switch (InstalledServerLocator.Resolve(game, executablePath, registry))
        {
            case GameServer.Global:
                return Global;
            case GameServer.Cn:
                // HYP 把 B 服也登记成 _cn，名字里带 bilibili 时更精确
                return executablePath.Contains("bilibili", StringComparison.OrdinalIgnoreCase) ? Bilibili : Cn;
        }

        var exeName = Path.GetFileName(executablePath);
        if (string.Equals(exeName, "YuanShen.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Cn;
        }

        if (string.Equals(exeName, "GenshinImpact.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Global;
        }

        return GameExecutableFinder.PriorityOf(Path.GetDirectoryName(executablePath) ?? string.Empty) switch
        {
            0 => Cn,
            1 => Bilibili,
            3 => Global,
            _ => null,
        };
    }

    /// <summary>读 config.ini 里的区服：优先 game_biz 后缀，其次 cps / channel。</summary>
    public static string? FromConfigIni(string iniPath)
    {
        string text;
        try
        {
            if (!File.Exists(iniPath))
            {
                return null;
            }

            text = File.ReadAllText(iniPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var biz = Value(text, "game_biz");
        if (biz.Length > 0)
        {
            if (biz.EndsWith("bilibili", StringComparison.OrdinalIgnoreCase))
            {
                return Bilibili;
            }

            if (biz.EndsWith("global", StringComparison.OrdinalIgnoreCase) || biz.EndsWith("overseas", StringComparison.OrdinalIgnoreCase))
            {
                return Global;
            }

            if (biz.EndsWith("_cn", StringComparison.OrdinalIgnoreCase))
            {
                return Cn;
            }
        }

        var cps = Value(text, "cps");
        if (cps.Equals("bilibili", StringComparison.OrdinalIgnoreCase) || Value(text, "channel") == "14")
        {
            return Bilibili;
        }

        return cps.ToLowerInvariant() switch
        {
            "mihoyo" => Cn,
            "hoyoverse" => Global,
            _ => null,
        };
    }

    private static string Value(string text, string key)
    {
        var match = Regex.Match(text, $@"^\s*{Regex.Escape(key)}\s*=\s*(.*?)\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
