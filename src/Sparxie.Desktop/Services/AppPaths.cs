namespace Sparxie.Desktop.Services;

/// <summary>
/// 桌面端用户数据根目录（config.json、logs、cache）：默认放在程序目录（便携），不写 AppData。
/// 程序目录不可写（装在 Program Files 或其它需要管理员权限的位置）时，回退到 %LOCALAPPDATA%\Sparxie，保证配置、日志、缓存都能写。
/// 注意：这只是“用户数据”。插件（plugins\、plugin-trust.json、plugin-data\、recovery\）仍在程序目录，
/// 因为以管理员权限运行的 Broker / SessionHost 从那里读取并信任它们；装在受保护目录时，管理插件需要管理员权限。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> Root = new(() => ResolveRoot(
        Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    public static string DataRoot => Root.Value;

    /// <summary>程序目录（权威的插件根目录所在位置）；可能和 <see cref="DataRoot"/> 不同。</summary>
    public static string InstallRoot => Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve();

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    public static string BackgroundCacheDirectory => Path.Combine(DataRoot, "cache", "backgrounds");

    /// <summary>程序目录能写就用它，否则用 <paramref name="localAppData"/>\Sparxie（纯函数，便于测试）。</summary>
    public static string ResolveRoot(string baseDirectory, string localAppData)
    {
        try
        {
            Directory.CreateDirectory(baseDirectory);
            var probe = Path.Combine(baseDirectory, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return baseDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(localAppData, "Sparxie");
        }
    }

    /// <summary>
    /// config.json 的位置：用户数据根目录下。回退到 AppData 时，如果程序目录里已有（只读可见的）旧配置而新位置还没有，
    /// 就拷贝一份过去，用户的配置不会因为换了位置而"消失"。
    /// </summary>
    public static string ConfigFilePath()
    {
        var target = Path.Combine(DataRoot, "config.json");
        var legacy = Path.Combine(InstallRoot, "config.json");
        try
        {
            if (!string.Equals(DataRoot, InstallRoot, StringComparison.OrdinalIgnoreCase) && !File.Exists(target) && File.Exists(legacy))
            {
                Directory.CreateDirectory(DataRoot);
                File.Copy(legacy, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移失败不影响使用：按"没有配置"启动
        }

        return target;
    }
}
