namespace Sparxie.LauncherCore;

public enum LauncherCommandKind
{
    Help,
    List,
    Launch,
}

public sealed record LauncherCommand(LauncherCommandKind Kind, string? ProfileSelector = null);

public sealed record LauncherCommandParseResult(
    bool Success,
    LauncherCommand? Command,
    string? Error)
{
    public static LauncherCommandParseResult Ok(LauncherCommand command) => new(true, command, null);

    public static LauncherCommandParseResult Fail(string error) => new(false, null, error);
}

public static class LauncherCommandParser
{
    public static LauncherCommandParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return LauncherCommandParseResult.Ok(new LauncherCommand(LauncherCommandKind.Help));
        }

        var command = args[0].Trim();
        if (command is "help" or "--help" or "-h")
        {
            return args.Count == 1
                ? LauncherCommandParseResult.Ok(new LauncherCommand(LauncherCommandKind.Help))
                : LauncherCommandParseResult.Fail("help 不接受额外参数");
        }

        if (string.Equals(command, "list", StringComparison.OrdinalIgnoreCase))
        {
            return args.Count == 1
                ? LauncherCommandParseResult.Ok(new LauncherCommand(LauncherCommandKind.List))
                : LauncherCommandParseResult.Fail("list 不接受额外参数");
        }

        if (string.Equals(command, "launch", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count > 2)
            {
                return LauncherCommandParseResult.Fail("launch 最多接受一个 Profile ID 或名称");
            }

            if (args.Count == 2 && string.IsNullOrWhiteSpace(args[1]))
            {
                return LauncherCommandParseResult.Fail("Profile ID 或名称不能为空");
            }

            return LauncherCommandParseResult.Ok(new LauncherCommand(
                LauncherCommandKind.Launch,
                args.Count == 2 ? args[1] : null));
        }

        return LauncherCommandParseResult.Fail($"未知命令: {args[0]}");
    }
}

public enum LauncherInputCommandKind
{
    Empty,
    Quit,
    SetTargetFps,
}

public readonly record struct LauncherInputCommand(
    LauncherInputCommandKind Kind,
    int TargetFps = 0);

public static class LauncherInputParser
{
    public static bool TryParse(
        string? line,
        out LauncherInputCommand command,
        out string? error)
    {
        command = default;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            command = new LauncherInputCommand(LauncherInputCommandKind.Empty);
            return true;
        }

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0] is "quit" or "exit")
        {
            command = new LauncherInputCommand(LauncherInputCommandKind.Quit);
            return true;
        }

        if (parts.Length != 2 || !string.Equals(parts[0], "fps", StringComparison.OrdinalIgnoreCase))
        {
            error = "输入格式：fps <10-1000> 或 quit";
            return false;
        }

        if (!int.TryParse(parts[1], out var fps) || fps is < 10 or > 1000)
        {
            error = "目标 FPS 必须在 10–1000 之间";
            return false;
        }

        command = new LauncherInputCommand(LauncherInputCommandKind.SetTargetFps, fps);
        return true;
    }
}
