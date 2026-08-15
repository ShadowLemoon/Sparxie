using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>config.json 的持久化序列化契约：camelCase 字段 + camelCase 枚举。</summary>
public static class ConfigJsonOptions
{
    public static readonly JsonSerializerOptions Serialize = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };
}
