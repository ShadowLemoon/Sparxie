using System.Security.Cryptography;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Infrastructure.Plugins;

public enum PluginAvailability
{
    /// <summary>manifest 合法、DLL 存在，且哈希与用户确认过的记录一致。</summary>
    Ready,

    /// <summary>用户尚未确认（默认状态）。</summary>
    Untrusted,

    /// <summary>DLL 或 manifest 与确认时不同，需要重新确认。</summary>
    HashChanged,

    /// <summary>manifest 的 abiMajor 高于宿主支持的版本。</summary>
    AbiTooNew,

    /// <summary>manifest 非法或缺少 module.dll。</summary>
    Invalid,
}

public sealed record PluginEntry(
    string Id,
    string Directory,
    PluginAvailability Availability,
    string? Reason,
    PluginManifest? Manifest,
    string? DllSha256,
    string? ManifestSha256,
    long DllSize);

/// <summary>
/// 只读的插件目录：扫描 &lt;root&gt;\*\plugin.json，不执行任何插件代码，不递归，忽略符号链接/重解析点。
/// 只有 <see cref="PluginAvailability.Ready"/> 的插件会出现在 <see cref="All"/> 里，其余按"不存在"处理（白名单一律拒绝）。
/// 每次调用都会重新读取磁盘，下架立即生效。
/// </summary>
public sealed class PluginCatalog(string pluginsRoot, PluginTrustStore trust) : IGameCatalog
{
    public const string ManifestFileName = "plugin.json";

    public string PluginsRoot => pluginsRoot;

    public IReadOnlyList<PluginEntry> Scan()
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        var trusted = trust.Load().ToDictionary(record => record.Id, StringComparer.Ordinal);
        var entries = new List<PluginEntry>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(pluginsRoot).OrderBy(path => path, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // 读不了目录就当没有插件
        }

        foreach (var dir in directories)
        {
            if (IsReparsePoint(dir))
            {
                continue;
            }

            try
            {
                entries.Add(ScanOne(dir, trusted));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 一个插件的任何异常都只让它自己不可用，不能拖垮主程序启动。
                entries.Add(new PluginEntry(Path.GetFileName(dir), dir, PluginAvailability.Invalid, $"无法读取插件：{ex.Message}", null, null, null, 0));
            }
        }

        return entries;
    }

    public IReadOnlyList<GameDescriptor> All => Scan()
        .Where(entry => entry.Availability == PluginAvailability.Ready)
        .Select(ToDescriptor)
        .ToList();

    public GameDescriptor? Find(string? id) => All.FirstOrDefault(item => item.Id == id);

    public PluginAvailability? Availability(string id) => Scan().FirstOrDefault(entry => entry.Id == id)?.Availability;

    private static GameDescriptor ToDescriptor(PluginEntry entry) =>
        new(entry.Id, entry.Manifest!.DisplayName, entry.Manifest.ExeNames, entry.Manifest.Schema,
            PluginAssets.Resolve(entry.Directory, entry.Manifest.Icon),
            PluginAssets.Resolve(entry.Directory, entry.Manifest.Background),
            entry.Manifest.AutoFind?.UninstallNames,
            entry.Manifest.BackgroundUrl,
            entry.Manifest.BackgroundApi,
            entry.Manifest.AutoFind?.Registry);

    private static PluginEntry ScanOne(string dir, Dictionary<string, PluginTrustRecord> trusted)
    {
        var id = Path.GetFileName(dir);
        PluginEntry Fail(PluginAvailability availability, string reason, PluginManifest? manifest = null) =>
            new(id, dir, availability, reason, manifest, null, null, 0);

        var manifestPath = Path.Combine(dir, ManifestFileName);
        var dllPath = Path.Combine(dir, PluginManifest.ModuleFileName);
        if (!File.Exists(manifestPath) || IsReparsePoint(manifestPath))
        {
            return Fail(PluginAvailability.Invalid, "缺少 plugin.json");
        }

        if (!File.Exists(dllPath) || IsReparsePoint(dllPath))
        {
            return Fail(PluginAvailability.Invalid, $"缺少 {PluginManifest.ModuleFileName}");
        }

        byte[] manifestBytes;
        string dllHash;
        long dllSize;
        try
        {
            if (new FileInfo(manifestPath).Length > PluginManifest.MaxManifestBytes)
            {
                return Fail(PluginAvailability.Invalid, "plugin.json 过大");
            }

            manifestBytes = File.ReadAllBytes(manifestPath);
            using var stream = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            dllSize = stream.Length;
            dllHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(PluginAvailability.Invalid, $"无法读取插件文件: {ex.Message}");
        }

        if (!PluginManifest.TryParse(manifestBytes, id, out var manifest, out var error))
        {
            return Fail(PluginAvailability.Invalid, error);
        }

        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        PluginEntry Result(PluginAvailability availability, string? reason = null) =>
            new(id, dir, availability, reason, manifest, dllHash, manifestHash, dllSize);

        if (manifest.AbiMajor > PluginManifest.SupportedAbiMajor)
        {
            return Result(PluginAvailability.AbiTooNew, $"插件需要 ABI {manifest.AbiMajor}，宿主只支持 {PluginManifest.SupportedAbiMajor}");
        }

        if (manifest.AbiMajor != PluginManifest.SupportedAbiMajor)
        {
            return Result(PluginAvailability.Invalid, $"abiMajor 非法: {manifest.AbiMajor}");
        }

        if (!trusted.TryGetValue(id, out var record))
        {
            return Result(PluginAvailability.Untrusted);
        }

        return record.DllSha256 == dllHash && record.ManifestSha256 == manifestHash && record.Size == dllSize
            ? Result(PluginAvailability.Ready)
            : Result(PluginAvailability.HashChanged);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
