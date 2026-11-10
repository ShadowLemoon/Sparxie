using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.LauncherCore;

public enum ExecutablePathIssue
{
    None,

    /// <summary>还没有填写路径。</summary>
    Empty,

    /// <summary>不是完整路径。</summary>
    NotFullyQualified,

    /// <summary>文件名不在该游戏的白名单内。</summary>
    NotAllowed,

    /// <summary>文件不存在。</summary>
    NotFound,
}

/// <summary>游戏 EXE 路径的即时检查：设置页输入提示与主页「定位游戏」共用，规则与 ProfileManager / 会话边界一致。</summary>
public static class ExecutablePathCheck
{
    public static ExecutablePathIssue Check(GameType game, string? path, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ExecutablePathIssue.Empty;
        }

        var input = path.Trim();
        if (!Path.IsPathFullyQualified(input))
        {
            return ExecutablePathIssue.NotFullyQualified;
        }

        if (!GameExecutables.IsAllowed(game, input))
        {
            return ExecutablePathIssue.NotAllowed;
        }

        return (fileExists ?? File.Exists)(input) ? ExecutablePathIssue.None : ExecutablePathIssue.NotFound;
    }

    /// <summary>面向用户的提示文案；<see cref="ExecutablePathIssue.None"/> 返回空串。</summary>
    public static string Describe(GameType game, ExecutablePathIssue issue) => issue switch
    {
        ExecutablePathIssue.Empty => "尚未设置游戏路径",
        ExecutablePathIssue.NotFullyQualified => "请填写完整路径（如 D:\\Games\\…\\" + Example(game) + "）",
        ExecutablePathIssue.NotAllowed => $"文件名不属于该游戏，可用：{string.Join("、", GameExecutables.For(game))}",
        ExecutablePathIssue.NotFound => "找不到该文件，请确认路径",
        _ => string.Empty,
    };

    private static string Example(GameType game) => GameExecutables.For(game).FirstOrDefault() ?? "game.exe";
}
