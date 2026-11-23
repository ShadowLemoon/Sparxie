using System.IO.Compression;
using System.Text.Json;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Plugins;

public sealed record PluginInstallResult(bool Success, string Message, string? Id = null, bool Replaced = false);

/// <summary>
/// 把压缩包（.zip）或文件夹导入成 &lt;plugins&gt;\&lt;id&gt;\。只做"拷贝 + 校验"，<b>不会启用</b>：导入后仍要用户在设置页看哈希并确认。
/// 先放进暂存目录并校验 plugin.json 与 module.dll，通过后才原子地换进 plugins 目录；已有同 id 插件时整体替换（哈希变了，需要重新确认）。
/// 压缩包按 zip-slip、文件数、总大小做限制；文件夹里有链接（重解析点）直接拒绝。
/// </summary>
public static class PluginInstaller
{
    public const int MaxFiles = 200;
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    public static PluginInstallResult Install(string source, string pluginsRoot, string stagingRoot)
    {
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var extractError = Directory.Exists(source) ? CopyDirectory(source, staging)
                : File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ExtractZip(source, staging)
                : "请选择 .zip 压缩包或插件文件夹";
            if (extractError is not null)
            {
                return new PluginInstallResult(false, extractError);
            }

            if (FindPluginRoot(staging) is not { } root)
            {
                return new PluginInstallResult(false, "没有找到 plugin.json（应在压缩包/文件夹的根目录，或唯一的一层子目录里）");
            }

            var (id, problem) = Validate(root);
            if (id is null)
            {
                return new PluginInstallResult(false, problem);
            }

            return Place(root, id, pluginsRoot, stagingRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PluginInstallResult(false, $"导入失败：{ex.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>通过时返回 (id, 空)；不通过返回 (null, 原因)。</summary>
    private static (string? Id, string Problem) Validate(string root)
    {
        var manifestPath = Path.Combine(root, PluginCatalog.ManifestFileName);
        if (new FileInfo(manifestPath).Length > PluginManifest.MaxManifestBytes)
        {
            return (null, "plugin.json 过大");
        }

        var bytes = File.ReadAllBytes(manifestPath);
        string id;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            id = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException ex)
        {
            return (null, $"plugin.json 无法解析：{ex.Message}");
        }

        if (!GameIds.IsValidId(id) || GameIds.TryGetBuiltIn(id, out _))
        {
            return (null, $"插件 id 非法：{id}（应形如 ext.厂商.游戏）");
        }

        if (!PluginManifest.TryParse(bytes, id, out _, out var error))
        {
            return (null, error);
        }

        return File.Exists(Path.Combine(root, PluginManifest.ModuleFileName))
            ? (id, string.Empty)
            : (null, $"缺少 {PluginManifest.ModuleFileName}");
    }

    private static PluginInstallResult Place(string root, string id, string pluginsRoot, string stagingRoot)
    {
        Directory.CreateDirectory(pluginsRoot);
        var target = Path.Combine(pluginsRoot, id);
        var replaced = Directory.Exists(target);
        string? backup = null;
        if (replaced)
        {
            backup = Path.Combine(stagingRoot, "old-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(target, backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new PluginInstallResult(false, $"无法替换已有插件（文件可能正被占用，请先退出游戏）：{ex.Message}");
            }
        }

        try
        {
            Directory.Move(root, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (backup is not null)
            {
                try { Directory.Move(backup, target); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { }
            }

            return new PluginInstallResult(false, $"导入失败：{ex.Message}");
        }

        if (backup is not null)
        {
            TryDelete(backup);
        }

        return new PluginInstallResult(true, replaced ? "已替换同名插件" : "已导入", id, replaced);
    }

    private static string? FindPluginRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, PluginCatalog.ManifestFileName)))
        {
            return staging;
        }

        var subdirectories = Directory.GetDirectories(staging);
        return subdirectories.Length == 1 && File.Exists(Path.Combine(subdirectories[0], PluginCatalog.ManifestFileName))
            ? subdirectories[0]
            : null;
    }

    private static string? ExtractZip(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        if (files.Count == 0 || files.Count > MaxFiles || files.Sum(entry => entry.Length) > MaxTotalBytes)
        {
            return $"压缩包为空，或文件超过 {MaxFiles} 个 / 总大小超过 {MaxTotalBytes / 1024 / 1024} MB";
        }

        var rootPath = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long written = 0;
        foreach (var entry in files)
        {
            var full = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return $"压缩包含越界路径：{entry.FullName}";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var input = entry.Open();
            using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaxTotalBytes)
                {
                    return "压缩包解压后过大";
                }

                output.Write(buffer, 0, read);
            }
        }

        return null;
    }

    private static string? CopyDirectory(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        var count = 0;
        long total = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return $"文件夹里含有链接，已拒绝：{Path.GetRelativePath(sourceRoot, path)}";
            }

            var relative = Path.GetRelativePath(sourceRoot, path);
            var target = Path.Combine(destination, relative);
            if (Directory.Exists(path))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (++count > MaxFiles || (total += new FileInfo(path).Length) > MaxTotalBytes)
            {
                return $"文件夹文件超过 {MaxFiles} 个或总大小超过 {MaxTotalBytes / 1024 / 1024} MB";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暂存目录清理失败不影响结果
        }
    }
}
