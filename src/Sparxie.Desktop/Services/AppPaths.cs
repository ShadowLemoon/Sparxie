namespace Sparxie.Desktop.Services;

/// <summary>
/// 桌面端数据根目录：与 config.json / desktop.json 一样放在程序目录（便携），不写 AppData。
/// 仅当程序目录不可写（例如装在 Program Files 下）时才回退到 %LOCALAPPDATA%\Sparxie。
/// </summary>
internal static class AppPaths
{
    private static readonly Lazy<string> Root = new(ResolveRoot);

    public static string DataRoot => Root.Value;

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    public static string BackgroundCacheDirectory => Path.Combine(DataRoot, "cache", "backgrounds");

    private static string ResolveRoot()
    {
        var baseDirectory = Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve();
        try
        {
            var probe = Path.Combine(baseDirectory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return baseDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sparxie");
        }
    }
}
