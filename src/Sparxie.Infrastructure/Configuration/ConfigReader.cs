using System.Text.Json;
using System.Text.Json.Nodes;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Infrastructure.Configuration;

/// <summary><see cref="ConfigReader.Read"/> 从 config.json 字节里读出的结果。</summary>
/// <param name="Config">可用的部分；整份文件读不了时为 null。</param>
/// <param name="WholeFileError">整份文件读不了的原因：不是 JSON、根不是对象、schemaVersion 不对、没有 profiles 列表。</param>
/// <param name="Repairs">改回默认值或丢掉的字段。</param>
/// <param name="Quarantined">修不了、整个移出的 Profile。</param>
public sealed record ConfigReadResult(
    AppConfig? Config,
    string? WholeFileError,
    IReadOnlyList<string> Repairs,
    IReadOnlyList<string> Quarantined)
{
    public bool WholeFileReadable => WholeFileError is null && Config is not null;

    /// <summary>读出的结果和文件内容不一致：写回之前必须先保留原文件。</summary>
    public bool Changed => Repairs.Count > 0 || Quarantined.Count > 0;
}

/// <summary>
/// 逐个 Profile 读取 config.json，一个坏 Profile 不会让整份文件作废：
/// 坏字段改回默认值或丢掉，修不了的 Profile 单独移出，只有根本不是配置的文件才算整份读不了。
/// </summary>
public static class ConfigReader
{
    public static ConfigReadResult Read(byte[] bytes)
    {
        ReadOnlyMemory<byte> json = bytes;
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..]; // 手工编辑器存出来的 BOM
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Unreadable($"不是有效的 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable("根节点不是对象");
            }

            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schema) || schema != AppConfig.CurrentSchemaVersion)
            {
                return Unreadable($"不支持的 schemaVersion: {(root.TryGetProperty("schemaVersion", out var v) ? v.ToString() : "缺失")}");
            }

            if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            {
                return Unreadable("profiles 缺失或不是列表");
            }

            var repairs = new List<string>();
            var quarantined = new List<string>();
            var config = new AppConfig { SchemaVersion = schema };

            if (root.TryGetProperty("selectedProfileId", out var selected) && selected.ValueKind == JsonValueKind.String)
            {
                config.SelectedProfileId = selected.GetString();
            }

            if (root.TryGetProperty("desktop", out var desktop) && desktop.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    config.Desktop = desktop.Deserialize<DesktopSettings>(ConfigJsonOptions.Read);
                }
                catch (JsonException ex)
                {
                    repairs.Add($"界面偏好无法读取，已用默认值：{ex.Message}");
                }
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in profiles.EnumerateArray())
            {
                index++;
                var notes = new List<string>();
                var profile = ReadProfile(element, index, notes, quarantined);
                if (profile is null)
                {
                    continue;
                }

                ConfigRepair.RepairProfile(profile, notes);
                var errors = AppConfigValidator.ValidateProfile(profile);
                if (errors.Count > 0)
                {
                    quarantined.Add($"{Describe(profile.Id, index)} 无法修复，已移出：{string.Join("；", errors)}");
                    continue;
                }

                if (!ids.Add(profile.Id))
                {
                    quarantined.Add($"{Describe(profile.Id, index)} 的 id 重复，已移出（保留先出现的）");
                    continue;
                }

                repairs.AddRange(notes);
                config.Profiles.Add(profile);
            }

            return new ConfigReadResult(config, null, repairs, quarantined);
        }
    }

    private static ConfigReadResult Unreadable(string why) => new(null, why, [], []);

    private static string Describe(string? id, int index) =>
        string.IsNullOrWhiteSpace(id) ? $"第 {index} 个配置" : $"配置“{id}”";

    private static GameProfile? ReadProfile(JsonElement element, int index, List<string> notes, List<string> quarantined)
    {
        string label = Describe(null, index);
        try
        {
            if (element.ValueKind != JsonValueKind.Object || JsonNode.Parse(element.GetRawText()) is not JsonObject node)
            {
                quarantined.Add($"{label} 不是对象，已移出");
                return null;
            }

            var id = Text(node["id"]);
            label = Describe(id, index);

            if (string.IsNullOrWhiteSpace(Text(node["displayName"])) && !string.IsNullOrWhiteSpace(id))
            {
                node["displayName"] = id;
                notes.Add($"{label} 没有可用的名称，改用 id");
            }

            if (Text(node["executablePath"]) is null)
            {
                node["executablePath"] = string.Empty;
                notes.Add($"{label} 的游戏路径缺失或无法读取，需要重新选择");
            }

            DropUnreadableFields<HoyoProfileSettings>(node, "hoyo", label, notes);
            DropUnreadableFields<LaunchSettings>(node, "launch", label, notes);

            if (node["settings"] is { } settings && settings is not JsonObject)
            {
                node.Remove("settings");
                notes.Add($"{label} 的插件设置不是对象，已丢弃");
            }

            var profile = node.Deserialize<GameProfile>(ConfigJsonOptions.Read);
            if (profile is null)
            {
                quarantined.Add($"{label} 为空，已移出");
            }

            return profile;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // ArgumentException：对象里有重复的键
            quarantined.Add($"{label} 无法读取，已移出：{ex.Message}");
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>逐个字段试读 <paramref name="name"/> 段，只丢掉类型不对的字段；整段不是对象时丢掉整段。</summary>
    private static void DropUnreadableFields<T>(JsonObject profile, string name, string label, List<string> notes)
    {
        if (profile[name] is not { } section)
        {
            return;
        }

        if (section is not JsonObject fields)
        {
            profile.Remove(name);
            notes.Add($"{label} 的 {name} 不是对象，已用默认值");
            return;
        }

        foreach (var field in fields.ToList())
        {
            try
            {
                _ = new JsonObject { [field.Key] = field.Value?.DeepClone() }.Deserialize<T>(ConfigJsonOptions.Read);
            }
            catch (JsonException)
            {
                fields.Remove(field.Key);
                notes.Add($"{label} 的 {name}.{field.Key} 无法读取（{field.Value?.ToJsonString() ?? "null"}），已用默认值");
            }
        }
    }
}
