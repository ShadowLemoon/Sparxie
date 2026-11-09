namespace Sparxie.Desktop.Services;

/// <summary>桌面端诊断日志。写入失败一律静默，避免掩盖原始异常。</summary>
internal static class DesktopLog
{
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(AppPaths.LogsDirectory, "desktop.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                System.IO.File.AppendAllText(Path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志失败不影响主流程。
        }
    }

    public static void Write(string context, Exception exception) =>
        Write($"{context}: {exception}");
}
