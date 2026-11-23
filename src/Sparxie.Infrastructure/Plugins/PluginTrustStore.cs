using System.Text.Json;
using Sparxie.Infrastructure.Configuration;

namespace Sparxie.Infrastructure.Plugins;

/// <summary>用户确认时的记录。Version / DisplayName / Caps 只用于“文件已变化”时展示差异（旧记录里没有，为 null）。</summary>
public sealed record PluginTrustRecord(
    string Id,
    string DllSha256,
    string ManifestSha256,
    long Size,
    DateTimeOffset ConfirmedAt,
    string? Version = null,
    string? DisplayName = null,
    IReadOnlyList<string>? Caps = null);

/// <summary>
/// plugin-trust.json：用户确认过的插件哈希。作用是发现文件变化（DLL 或 manifest 被替换），
/// 不是防同用户权限的篡改（app 目录本身可写）。文件损坏时按"无任何信任记录"处理，不抛异常。
/// </summary>
public sealed class PluginTrustStore(string path)
{
    private sealed record Document(int Version, List<PluginTrustRecord> Records);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Path { get; } = path;

    public IReadOnlyList<PluginTrustRecord> Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(Path), Options);
            // 内容不可信：丢弃 null/缺字段的记录，同 id 只留最后一条，保证调用方拿到的一定是干净的。
            return (document?.Records ?? [])
                .Where(record => record is { Id.Length: > 0, DllSha256.Length: > 0, ManifestSha256.Length: > 0 })
                .GroupBy(record => record.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    public PluginTrustRecord? Find(string id) => Load().FirstOrDefault(record => record.Id == id);

    /// <summary>用户确认后写入（覆盖同 id 的旧记录）。</summary>
    public void Confirm(PluginTrustRecord record) => Save(Load().Where(item => item.Id != record.Id).Append(record));

    public void Remove(string id) => Save(Load().Where(item => item.Id != id));

    private void Save(IEnumerable<PluginTrustRecord> records)
    {
        var document = new Document(1, records.OrderBy(item => item.Id, StringComparer.Ordinal).ToList());
        AtomicFile.WriteAllTextAtomic(Path, JsonSerializer.Serialize(document, Options));
    }
}
