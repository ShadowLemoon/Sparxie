using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;

namespace Sparxie.LauncherCore;

public enum LauncherCommandKind
{
    Help,
    List,
    Launch,
    ProfileShow,
    ProfileAdd,
    ProfileSet,
    ProfileSelect,
    ProfileRemove,
}

/// <summary>CLI 传入的 Profile 创建/修改字段；null 表示该字段未指定。</summary>
public sealed record ProfileMutation(
    string? Id = null,
    string? DisplayName = null,
    GameType? Game = null,
    string? ExecutablePath = null,
    bool? FpsUnlockEnabled = null,
    int? TargetFps = null,
    bool? BackgroundFpsLimitEnabled = null,
    int? BackgroundFps = null,
    ProcessPriority? ProcessPriority = null,
    bool? GenshinFollowInGamePreset = null,
    int? GenshinPreset30Fps = null,
    int? GenshinPreset60Fps = null,
    bool? GenshinTouchUiScaleOverrideEnabled = null,
    int? GenshinTouchUiScalePercent = null)
{
    public bool HasHoyoSettings =>
        FpsUnlockEnabled.HasValue
        || TargetFps.HasValue
        || BackgroundFpsLimitEnabled.HasValue
        || BackgroundFps.HasValue
        || ProcessPriority.HasValue;

    public bool HasGenshinSettings =>
        GenshinFollowInGamePreset.HasValue
        || GenshinPreset30Fps.HasValue
        || GenshinPreset60Fps.HasValue
        || GenshinTouchUiScaleOverrideEnabled.HasValue
        || GenshinTouchUiScalePercent.HasValue;

    public bool HasAnySettableField =>
        !string.IsNullOrWhiteSpace(DisplayName)
        || !string.IsNullOrWhiteSpace(ExecutablePath)
        || HasHoyoSettings
        || HasGenshinSettings;
}

public sealed record LauncherCommand(
    LauncherCommandKind Kind,
    string? ProfileSelector = null,
    ProfileMutation? ProfileMutation = null);

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
    private static readonly HashSet<string> CreateOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "name",
        "game",
        "exe",
        "fps",
        "target-fps",
        "background-fps-limit",
        "background-fps",
        "priority",
        "follow-in-game-preset",
        "preset-30-fps",
        "preset-60-fps",
        "touch-ui-scale-override",
        "touch-ui-scale",
    };

    private static readonly HashSet<string> SetOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "exe",
        "fps",
        "target-fps",
        "background-fps-limit",
        "background-fps",
        "priority",
        "follow-in-game-preset",
        "preset-30-fps",
        "preset-60-fps",
        "touch-ui-scale-override",
        "touch-ui-scale",
    };

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

        if (string.Equals(command, "profile", StringComparison.OrdinalIgnoreCase))
        {
            return ParseProfileCommand(args);
        }

        return LauncherCommandParseResult.Fail($"未知命令: {args[0]}");
    }

    private static LauncherCommandParseResult ParseProfileCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            return LauncherCommandParseResult.Fail("profile 需要子命令：list、show、add、set、select 或 remove");
        }

        var subcommand = args[1].Trim();
        if (string.Equals(subcommand, "list", StringComparison.OrdinalIgnoreCase))
        {
            return args.Count == 2
                ? LauncherCommandParseResult.Ok(new LauncherCommand(LauncherCommandKind.List))
                : LauncherCommandParseResult.Fail("profile list 不接受额外参数");
        }

        if (string.Equals(subcommand, "show", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSelectorOnly(args, LauncherCommandKind.ProfileShow, "profile show");
        }

        if (string.Equals(subcommand, "select", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSelectorOnly(args, LauncherCommandKind.ProfileSelect, "profile select");
        }

        if (string.Equals(subcommand, "remove", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSelectorOnly(args, LauncherCommandKind.ProfileRemove, "profile remove");
        }

        if (string.Equals(subcommand, "add", StringComparison.OrdinalIgnoreCase))
        {
            var mutation = ParseMutation(args, 2, CreateOptions, "profile add", out var error);
            if (error is not null)
            {
                return LauncherCommandParseResult.Fail(error);
            }

            if (mutation.Id is null || mutation.DisplayName is null || mutation.Game is null
                || mutation.ExecutablePath is null)
            {
                return LauncherCommandParseResult.Fail(
                    "profile add 必须提供 --id、--name、--game 和 --exe");
            }

            return LauncherCommandParseResult.Ok(new LauncherCommand(LauncherCommandKind.ProfileAdd, ProfileMutation: mutation));
        }

        if (string.Equals(subcommand, "set", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 4 || string.IsNullOrWhiteSpace(args[2]))
            {
                return LauncherCommandParseResult.Fail("profile set 需要 Profile ID 或名称以及至少一个修改选项");
            }

            var mutation = ParseMutation(args, 3, SetOptions, "profile set", out var error);
            if (error is not null)
            {
                return LauncherCommandParseResult.Fail(error);
            }

            if (!mutation.HasAnySettableField)
            {
                return LauncherCommandParseResult.Fail("profile set 至少需要一个可修改字段");
            }

            return LauncherCommandParseResult.Ok(new LauncherCommand(
                LauncherCommandKind.ProfileSet,
                args[2],
                mutation));
        }

        return LauncherCommandParseResult.Fail($"未知 profile 子命令: {args[1]}");
    }

    private static LauncherCommandParseResult ParseSelectorOnly(
        IReadOnlyList<string> args,
        LauncherCommandKind kind,
        string commandName)
    {
        if (args.Count != 3 || string.IsNullOrWhiteSpace(args[2]))
        {
            return LauncherCommandParseResult.Fail($"{commandName} 需要且仅需要一个 Profile ID 或名称");
        }

        return LauncherCommandParseResult.Ok(new LauncherCommand(kind, args[2]));
    }

    private static ProfileMutation ParseMutation(
        IReadOnlyList<string> args,
        int startIndex,
        HashSet<string> allowedOptions,
        string commandName,
        out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        for (var index = startIndex; index < args.Count; index++)
        {
            var token = args[index];
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("--", StringComparison.Ordinal)
                || token.Length == 2)
            {
                error = $"{commandName} 仅接受 --选项 值 格式";
                return new ProfileMutation();
            }

            var option = token[2..];
            if (!allowedOptions.Contains(option))
            {
                error = $"{commandName} 不支持选项: {token}";
                return new ProfileMutation();
            }

            if (!values.TryAdd(option, string.Empty))
            {
                error = $"{commandName} 的选项重复: {token}";
                return new ProfileMutation();
            }

            if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{commandName} 的 {token} 缺少值";
                return new ProfileMutation();
            }

            values[option] = args[index];
        }

        return CreateMutation(values, commandName, out error);
    }

    private static ProfileMutation CreateMutation(
        IReadOnlyDictionary<string, string> values,
        string commandName,
        out string? error)
    {
        error = null;
        var mutation = new ProfileMutation(
            Id: Value("id"),
            DisplayName: Value("name"),
            Game: ParseGame(Value("game"), "--game", commandName, ref error),
            ExecutablePath: Value("exe"),
            FpsUnlockEnabled: ParseOnOff(Value("fps"), "--fps", commandName, ref error),
            TargetFps: ParseInt(Value("target-fps"), "--target-fps", AppConfigValidator.MinFps, AppConfigValidator.MaxFps, commandName, ref error),
            BackgroundFpsLimitEnabled: ParseOnOff(Value("background-fps-limit"), "--background-fps-limit", commandName, ref error),
            BackgroundFps: ParseInt(Value("background-fps"), "--background-fps", AppConfigValidator.MinFps, AppConfigValidator.MaxFps, commandName, ref error),
            ProcessPriority: ParsePriority(Value("priority"), commandName, ref error),
            GenshinFollowInGamePreset: ParseOnOff(Value("follow-in-game-preset"), "--follow-in-game-preset", commandName, ref error),
            GenshinPreset30Fps: ParseInt(Value("preset-30-fps"), "--preset-30-fps", AppConfigValidator.MinFps, AppConfigValidator.MaxFps, commandName, ref error),
            GenshinPreset60Fps: ParseInt(Value("preset-60-fps"), "--preset-60-fps", AppConfigValidator.MinFps, AppConfigValidator.MaxFps, commandName, ref error),
            GenshinTouchUiScaleOverrideEnabled: ParseOnOff(Value("touch-ui-scale-override"), "--touch-ui-scale-override", commandName, ref error),
            GenshinTouchUiScalePercent: ParseInt(Value("touch-ui-scale"), "--touch-ui-scale", AppConfigValidator.MinTouchUiScalePercent, AppConfigValidator.MaxTouchUiScalePercent, commandName, ref error));

        return mutation;

        string? Value(string option) => values.TryGetValue(option, out var value) ? value : null;
    }

    private static GameType? ParseGame(string? value, string option, string commandName, ref string? error)
    {
        if (value is null || error is not null)
        {
            return null;
        }

        if (string.Equals(value, "genshin", StringComparison.OrdinalIgnoreCase))
        {
            return GameType.Genshin;
        }

        if (string.Equals(value, "starRail", StringComparison.OrdinalIgnoreCase))
        {
            return GameType.StarRail;
        }

        if (string.Equals(value, "zenlessZoneZero", StringComparison.OrdinalIgnoreCase))
        {
            return GameType.ZenlessZoneZero;
        }

        error = $"{commandName} 的 {option} 必须是 genshin、starRail 或 zenlessZoneZero";
        return null;
    }

    private static bool? ParseOnOff(string? value, string option, string commandName, ref string? error)
    {
        if (value is null || error is not null)
        {
            return null;
        }

        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        error = $"{commandName} 的 {option} 必须是 on 或 off";
        return null;
    }

    private static int? ParseInt(
        string? value,
        string option,
        int minimum,
        int maximum,
        string commandName,
        ref string? error)
    {
        if (value is null || error is not null)
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            error = $"{commandName} 的 {option} 必须在 {minimum}–{maximum} 之间";
            return null;
        }

        return parsed;
    }

    private static ProcessPriority? ParsePriority(string? value, string commandName, ref string? error)
    {
        if (value is null || error is not null)
        {
            return null;
        }

        if (string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessPriority.Normal;
        }

        if (string.Equals(value, "belowNormal", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessPriority.BelowNormal;
        }

        if (string.Equals(value, "aboveNormal", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessPriority.AboveNormal;
        }

        if (string.Equals(value, "high", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessPriority.High;
        }

        error = $"{commandName} 的 --priority 必须是 normal、belowNormal、aboveNormal 或 high";
        return null;
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

        if (!int.TryParse(parts[1], out var fps) || fps is < AppConfigValidator.MinFps or > AppConfigValidator.MaxFps)
        {
            error = $"目标 FPS 必须在 {AppConfigValidator.MinFps}–{AppConfigValidator.MaxFps} 之间";
            return false;
        }

        command = new LauncherInputCommand(LauncherInputCommandKind.SetTargetFps, fps);
        return true;
    }
}
