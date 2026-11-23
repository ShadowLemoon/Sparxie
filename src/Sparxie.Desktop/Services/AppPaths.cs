namespace Sparxie.Desktop.Services;

/// <summary>
/// 桌面端数据根目录（config.json、logs、cache）：始终是程序目录（绿色便携），不会悄悄写到 AppData。
/// 程序目录写不进去（装在需要管理员权限的位置）时不做静默回退，而是在启动时明确提示用户，见 <see cref="IsWritable"/>。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<bool> Writable = new(() => CanWrite(DataRoot));

    public static string DataRoot => Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve();

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    public static string BackgroundCacheDirectory => Path.Combine(DataRoot, "cache", "backgrounds");

    public static string ConfigFilePath() => Path.Combine(DataRoot, "config.json");

    /// <summary>数据目录当前能不能写（进程内只探测一次）。不能写时配置、日志、缓存都无法保存，界面要明确告诉用户。</summary>
    public static bool IsWritable => Writable.Value;

    /// <summary>用创建再删除一个临时文件的方式探测目录是否可写（纯函数，便于测试）。</summary>
    public static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
