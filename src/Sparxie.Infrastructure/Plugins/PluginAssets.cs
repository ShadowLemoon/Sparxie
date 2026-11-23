namespace Sparxie.Infrastructure.Plugins;

/// <summary>
/// 插件自带的图标/背景图片。图片不参与信任哈希，所以只接受常见位图扩展名、限制大小、拒绝重解析点，
/// 并且只返回插件目录内的文件（文件名不能带路径）。
/// </summary>
public static class PluginAssets
{
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    public static bool IsValidFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && Path.GetFileName(name) == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>返回图片完整路径；不存在、过大或是链接时返回 null（界面按"没有"处理）。</summary>
    public static string? Resolve(string pluginDirectory, string? fileName)
    {
        if (!IsValidFileName(fileName))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(Path.Combine(pluginDirectory, fileName!));
            return info.Exists && info.Length is > 0 and <= MaxBytes && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? info.FullName
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
