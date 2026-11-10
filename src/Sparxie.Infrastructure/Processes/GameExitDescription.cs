namespace Sparxie.Infrastructure.Processes;

/// <summary>Exited 事件的说明文字：游戏退出码（可得时）与本次运行时长，便于区分正常退出 / 被结束 / 崩溃。</summary>
public static class GameExitDescription
{
    public static string Describe(int? exitCode, TimeSpan elapsed)
    {
        var duration = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        return exitCode is { } code
            ? $"游戏已退出，退出码 {code}，运行 {duration}"
            : $"游戏已退出，运行 {duration}";
    }
}
