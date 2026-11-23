namespace Sparxie.Desktop.Services;

/// <summary>拖放导入：从拖进来的路径里挑出能导入的（.zip 压缩包或文件夹），其余的列为不支持。</summary>
public static class PluginDropFilter
{
    public const int MaxItems = 8;

    public sealed record Result(IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected, bool TooMany);

    public static Result Pick(IEnumerable<string?> paths)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                accepted.Add(path);
            }
            else
            {
                rejected.Add(Path.GetFileName(path));
            }
        }

        var tooMany = accepted.Count > MaxItems;
        return new Result(tooMany ? accepted.Take(MaxItems).ToList() : accepted, rejected, tooMany);
    }
}
