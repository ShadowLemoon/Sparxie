using System.IO.Compression;
using System.Text.Json;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Plugins;

public enum PluginInstallFailure
{
    None,
    NotSupported,
    TooLarge,
    UnsafePath,
    LinkInside,
    NoManifest,
    ManifestInvalid,
    NoModule,
    InvalidId,
    Locked,
    Io,
    AccessDenied,
}

/// <summary>导入与已有插件的关系：新装 / 升级 / 同版本重装 / 降级 / 内容完全相同。</summary>
public enum PluginInstallKind
{
    New,
    Upgrade,
    Reinstall,
    Downgrade,
    Identical,
}

public sealed record PluginInstallResult(bool Success, string Message, string? Id = null, bool Replaced = false, PluginInstallFailure Failure = PluginInstallFailure.None);

/// <summary>第一阶段（检查）的结果：包已解到暂存目录并通过校验，但还没有动 plugins 目录。用户确认后 <see cref="PluginInstaller.Commit"/>，放弃则 <see cref="PluginInstaller.Discard"/>。</summary>
public sealed record PluginInspection(
    bool Success,
    string Message,
    PluginInstallFailure Failure = PluginInstallFailure.None,
    string? Id = null,
    string? DisplayName = null,
    string? Version = null,
    string? ExistingVersion = null,
    PluginInstallKind Kind = PluginInstallKind.New,
    string? StagingDirectory = null,
    string? PluginRoot = null);

/// <summary>
/// 把压缩包（.zip）或文件夹导入成 &lt;plugins&gt;\&lt;id&gt;\。只做"拷贝 + 校验"，<b>不会启用</b>：导入后仍要用户在设置页看哈希并确认。
/// 两段式：<see cref="Inspect"/> 先放进暂存目录并校验 plugin.json 与 module.dll、判断与已有插件的关系（新装/升级/降级/相同）；
/// <see cref="Commit"/> 才原子地换进 plugins 目录（已有同 id 插件时整体替换，哈希变了需要重新确认）。
/// 压缩包按 zip-slip、文件数、总大小做限制；文件夹里有链接（重解析点）直接拒绝。
/// </summary>
public static class PluginInstaller
{
    public const int MaxFiles = 200;
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>一步导入（检查通过就直接提交）。</summary>
    public static PluginInstallResult Install(string source, string pluginsRoot, string stagingRoot)
    {
        var inspection = Inspect(source, pluginsRoot, stagingRoot);
        return inspection.Success ? Commit(inspection, pluginsRoot, stagingRoot) : new PluginInstallResult(false, inspection.Message, Failure: inspection.Failure);
    }

    public static PluginInspection Inspect(string source, string pluginsRoot, string stagingRoot)
    {
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var keep = false;
        try
        {
            Directory.CreateDirectory(staging);
            var extractError = Directory.Exists(source) ? CopyDirectory(source, staging)
                : File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ExtractZip(source, staging)
                : "请选择 .zip 压缩包或插件文件夹";
            if (extractError is not null)
            {
                return new PluginInspection(false, extractError, KindOfExtractError(extractError));
            }

            if (FindPluginRoot(staging) is not { } root)
            {
                return new PluginInspection(false, "没有找到 plugin.json（应在压缩包/文件夹的根目录，或唯一的一层子目录里）", PluginInstallFailure.NoManifest);
            }

            var (id, problem) = Validate(root);
            if (id is null)
            {
                return new PluginInspection(false, problem, problem.StartsWith("缺少 ", StringComparison.Ordinal) ? PluginInstallFailure.NoModule
                    : problem.StartsWith("插件 id 非法", StringComparison.Ordinal) ? PluginInstallFailure.InvalidId : PluginInstallFailure.ManifestInvalid);
            }

            PluginManifest.TryParse(File.ReadAllBytes(Path.Combine(root, PluginCatalog.ManifestFileName)), id, out var manifest, out _);
            var (kind, existingVersion) = Compare(Path.Combine(pluginsRoot, id), root, manifest.Version);
            keep = true;
            return new PluginInspection(true, string.Empty, PluginInstallFailure.None, id, manifest.DisplayName, manifest.Version, existingVersion, kind, staging, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PluginInspection(false, $"导入失败：{ex.Message}", PluginInstallFailure.Io);
        }
        finally
        {
            if (!keep)
            {
                TryDelete(staging);
            }
        }
    }

    /// <summary>第二阶段：把检查通过的包换进 plugins 目录。无论成败都会清理暂存目录。</summary>
    public static PluginInstallResult Commit(PluginInspection inspection, string pluginsRoot, string stagingRoot)
    {
        if (!inspection.Success || inspection.PluginRoot is null || inspection.Id is null)
        {
            return new PluginInstallResult(false, inspection.Message, Failure: inspection.Failure);
        }

        try
        {
            return Place(inspection.PluginRoot, inspection.Id, pluginsRoot, stagingRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginInstallResult(false, $"导入失败：{ex.Message}", Failure: PluginInstallFailure.Io);
        }
        finally
        {
            Discard(inspection);
        }
    }

    /// <summary>放弃检查通过的包，删除暂存目录。</summary>
    public static void Discard(PluginInspection inspection)
    {
        if (inspection.StagingDirectory is { } staging)
        {
            TryDelete(staging);
        }
    }

    private static PluginInstallFailure KindOfExtractError(string message) =>
        message.Contains("越界", StringComparison.Ordinal) ? PluginInstallFailure.UnsafePath
        : message.Contains("链接", StringComparison.Ordinal) ? PluginInstallFailure.LinkInside
        : message.Contains("超过", StringComparison.Ordinal) || message.Contains("过大", StringComparison.Ordinal) ? PluginInstallFailure.TooLarge
        : PluginInstallFailure.NotSupported;

    /// <summary>和已安装的同 id 插件比较：新装 / 内容相同 / 升级 / 降级 / 同版本重装。</summary>
    private static (PluginInstallKind Kind, string? ExistingVersion) Compare(string existingDirectory, string newRoot, string? newVersion)
    {
        var existingManifest = Path.Combine(existingDirectory, PluginCatalog.ManifestFileName);
        if (!File.Exists(existingManifest))
        {
            return (PluginInstallKind.New, null);
        }

        string? existingVersion = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(existingManifest));
            existingVersion = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 旧的读不了就当没有版本信息
        }

        if (SameContent(existingDirectory, newRoot))
        {
            return (PluginInstallKind.Identical, existingVersion);
        }

        if (Version.TryParse(Normalize(newVersion), out var next) && Version.TryParse(Normalize(existingVersion), out var current))
        {
            return (next > current ? PluginInstallKind.Upgrade : next < current ? PluginInstallKind.Downgrade : PluginInstallKind.Reinstall, existingVersion);
        }

        return (PluginInstallKind.Reinstall, existingVersion);
    }

    private static string? Normalize(string? version) => version is null ? null : (version.Count(c => c == '.') == 0 ? version + ".0" : version);

    private static bool SameContent(string a, string b)
    {
        foreach (var name in new[] { PluginCatalog.ManifestFileName, PluginManifest.ModuleFileName })
        {
            var left = Path.Combine(a, name);
            var right = Path.Combine(b, name);
            if (!File.Exists(left) || !File.Exists(right) || new FileInfo(left).Length != new FileInfo(right).Length
                || !File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right)))
            {
                return false;
            }
        }

        return true;
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
        string? backup = null;
        var target = Path.Combine(pluginsRoot, id);
        var replaced = false;
        try
        {
            Directory.CreateDirectory(pluginsRoot);
            replaced = Directory.Exists(target);
            if (replaced)
            {
                // 备份放在目标旁边（同一个盘，改名即可）；以 . 开头，目录扫描会忽略。
                backup = Path.Combine(pluginsRoot, ".old-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.Move(target, backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    backup = null;
                    return ex is UnauthorizedAccessException && !IsLockedByProcess(target)
                        ? AccessDenied(pluginsRoot)
                        : new PluginInstallResult(false, $"无法替换已有插件（文件可能正被占用，请先退出游戏）：{ex.Message}", Failure: PluginInstallFailure.Locked);
                }
            }

            MoveDirectory(root, target);
        }
        catch (UnauthorizedAccessException)
        {
            RestoreBackup(backup, target);
            return AccessDenied(pluginsRoot);
        }
        catch (IOException ex)
        {
            RestoreBackup(backup, target);
            return new PluginInstallResult(false, $"导入失败：{ex.Message}", Failure: PluginInstallFailure.Io);
        }

        if (backup is not null)
        {
            TryDelete(backup);
        }

        return new PluginInstallResult(true, replaced ? "已替换同名插件" : "已导入", id, replaced);
    }

    private static PluginInstallResult AccessDenied(string pluginsRoot) => new(
        false,
        $"没有权限写入插件目录：{pluginsRoot}。Sparxie 装在需要管理员权限的位置时，请以管理员身份运行 Sparxie 后再导入，或手动把插件文件夹复制到该目录。",
        Failure: PluginInstallFailure.AccessDenied);

    /// <summary>目录里有文件正被进程占用时 Windows 也可能报“拒绝访问”：粗略区分，占用走“被占用”的提示。</summary>
    private static bool IsLockedByProcess(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RestoreBackup(string? backup, string target)
    {
        if (backup is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
            Directory.Move(backup, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 恢复失败时旧版本仍在 .old-* 里，不会丢
        }
    }

    /// <summary>同一个盘改名；暂存目录在别的盘（例如系统临时目录）时退回复制再删除。</summary>
    private static void MoveDirectory(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (IOException) when (!Directory.Exists(target))
        {
            // 跨盘：往下走复制
        }

        CopyTree(source, target);
        TryDelete(source);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
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
