namespace Sparxie.Infrastructure.Configuration;

/// <summary>
/// 用户数据根目录（config.json / logs / cache）。
/// 发布包采用 app 目录式布局：根目录是壳启动器 Sparxie.exe，程序本体在 app-&lt;版本&gt;\ 里，
/// 旧版本目录会被壳清理，所以数据必须放在 app-* 的上一级；直接从任意目录运行（开发/旧布局）时仍取程序所在目录。
/// </summary>
public static class AppDataDirectory
{
    private const string AppDirectoryPrefix = "app-";

    public static string Resolve()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(baseDirectory);
        if (name.StartsWith(AppDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
            && Directory.GetParent(baseDirectory) is { } parent)
        {
            return parent.FullName;
        }

        return baseDirectory;
    }
}
