using System.Net;
using System.Text.Json;

namespace Sparxie.Infrastructure.Plugins;

/// <summary>manifest 里的 JSON 接口：宿主请求 <see cref="Url"/>，按 <see cref="Path"/> 取出背景图片地址（点分隔，数字表示数组下标，如 data.0.url）。</summary>
public sealed class BackgroundApiSpec
{
    public string Url { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

/// <summary>manifest 里的注册表查找项：读 <see cref="Hive"/>（HKCU / HKLM）下 <see cref="Key"/> 的 <see cref="Value"/>，得到安装目录（或 EXE 路径）。</summary>
public sealed class RegistryLookupSpec
{
    public string Hive { get; set; } = "HKCU";

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>可选：在读到的目录下再拼的子目录（如 "game"）。</summary>
    public string? Subdirectory { get; set; }

    public static bool IsValid(RegistryLookupSpec? spec) =>
        spec is not null
        && (spec.Hive is "HKCU" or "HKLM")
        && !string.IsNullOrWhiteSpace(spec.Key) && spec.Key.Length <= 260 && !spec.Key.Contains("..", StringComparison.Ordinal)
        && !spec.Key.StartsWith('\\') && !spec.Key.EndsWith('\\')
        && !string.IsNullOrWhiteSpace(spec.Value) && spec.Value.Length <= 64
        && (spec.Subdirectory is null || (spec.Subdirectory.Length <= 64 && !spec.Subdirectory.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(spec.Subdirectory)));
}

/// <summary>宿主替插件发起的网络请求的限制：只允许 https、不允许本机/内网字面地址。</summary>
public static class RemoteUrlPolicy
{
    public const int MaxApiBytes = 256 * 1024;

    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return false;
            }

            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4
                && (bytes[0] is 10 or 0 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>从 JSON 里按路径取字符串值（路径段是属性名或数组下标）。</summary>
    public static bool TryReadString(string json, string path, out string value)
    {
        value = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var node = doc.RootElement;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < node.GetArrayLength())
                {
                    node = node[index];
                }
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(part, out var child))
                {
                    node = child;
                }
                else
                {
                    return false;
                }
            }

            if (node.ValueKind != JsonValueKind.String || node.GetString() is not { Length: > 0 } text)
            {
                return false;
            }

            value = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>用文件头判断是不是我们接受的位图（png / jpg / webp / bmp）；不信任响应的 Content-Type。</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 12
        && ((bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            || (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            || (bytes[0] == 'B' && bytes[1] == 'M'));
}
