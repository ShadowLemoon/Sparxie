using System.Text.Json;
using System.Text.Json.Nodes;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>What <see cref="ConfigReader.Read"/> found in the bytes of a config.json.</summary>
/// <param name="Config">The profiles that are usable (null when the file as a whole is unreadable).</param>
/// <param name="WholeFileError">Why the file as a whole cannot be read: not JSON, not an object, another schemaVersion, no profiles list.</param>
/// <param name="Migrated">The silent one-time migration of the removed Endfield input 自动 changed something (no backup needed).</param>
/// <param name="Repairs">Field-level changes that put a value back to its default; the original must be backed up before they are saved.</param>
/// <param name="Quarantined">Profiles that cannot be repaired and were left out; the original must be backed up before they are saved.</param>
public sealed record ConfigReadResult(
    AppConfig? Config,
    string? WholeFileError,
    bool Migrated,
    IReadOnlyList<string> Repairs,
    IReadOnlyList<string> Quarantined)
{
    public bool WholeFileReadable => WholeFileError is null && Config is not null;

    /// <summary>The file is not exactly what the reader returns: the original has to be kept before the result is written.</summary>
    public bool Changed => Repairs.Count > 0 || Quarantined.Count > 0;
}

/// <summary>
/// Reads a config.json one profile at a time. One bad profile never makes the whole file unreadable (an Endfield size of 0×0, written by a client to mean "follow the stream", used to make the validator reject
/// the file and move every profile aside). A field that is out of range goes back to its default; a profile that cannot be repaired is left out and reported;
/// only a file that is not a config at all (not JSON, another schema) is unreadable as a whole.
/// </summary>
public static class ConfigReader
{
    public static ConfigReadResult Read(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
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
                return Unreadable("config.json 的根不是对象");
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
                    repairs.Add($"desktop 段无法读取，已用默认值：{ex.Message}");
                }
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var migrated = false;
            var index = 0;
            foreach (var element in profiles.EnumerateArray())
            {
                index++;
                var profile = ReadProfile(element, index, repairs, quarantined);
                if (profile is null)
                {
                    continue;
                }

                migrated |= AppConfigStore.MigrateEndfieldInput(profile);
                ConfigRepair.RepairProfile(profile, repairs);
                var errors = AppConfigValidator.ValidateProfile(profile);
                if (errors.Count > 0)
                {
                    quarantined.Add($"profile {Describe(profile.Id, index)} 无法修复，已移出：{string.Join("；", errors)}");
                    continue;
                }

                if (!ids.Add(profile.Id))
                {
                    quarantined.Add($"profile {Describe(profile.Id, index)} 的 id 重复，已移出（保留先出现的）");
                    continue;
                }

                config.Profiles.Add(profile);
            }

            return new ConfigReadResult(config, null, migrated, repairs, quarantined);
        }
    }

    private static ConfigReadResult Unreadable(string why) => new(null, why, false, [], []);

    private static string Describe(string? id, int index) => string.IsNullOrWhiteSpace(id) ? $"#{index}" : $"“{id}”";

    private static GameProfile? ReadProfile(JsonElement element, int index, List<string> repairs, List<string> quarantined)
    {
        if (element.ValueKind != JsonValueKind.Object || JsonNode.Parse(element.GetRawText()) is not JsonObject node)
        {
            quarantined.Add($"profile #{index} 不是对象，已移出");
            return null;
        }

        var id = node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : null;
        var label = Describe(id, index);

        // fields with a safe default
        if (node["displayName"] is not JsonValue || (node["displayName"] is JsonValue name && name.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)))
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                node["displayName"] = id;
                repairs.Add($"profile {label} 没有 displayName，改用 id");
            }
        }

        if (node["executablePath"] is null)
        {
            node["executablePath"] = string.Empty;
            repairs.Add($"profile {label} 没有 executablePath，按未完成配置处理");
        }

        if (node.TryGetPropertyValue("hoyo", out var hoyoNode) && hoyoNode is not null)
        {
            if (hoyoNode is JsonObject hoyo)
            {
                foreach (var property in hoyo.ToList())
                {
                    var probe = new JsonObject { [property.Key] = property.Value?.DeepClone() };
                    try
                    {
                        _ = probe.Deserialize<HoyoProfileSettings>(ConfigJsonOptions.Read);
                    }
                    catch (JsonException)
                    {
                        hoyo.Remove(property.Key);
                        repairs.Add($"profile {label} 的 hoyo.{property.Key} 无法读取（{property.Value?.ToJsonString()}），已用默认值");
                    }
                }
            }
            else
            {
                node.Remove("hoyo");
                repairs.Add($"profile {label} 的 hoyo 不是对象，已用默认值");
            }
        }

        try
        {
            var profile = node.Deserialize<GameProfile>(ConfigJsonOptions.Read);
            if (profile is null)
            {
                quarantined.Add($"profile {label} 为空，已移出");
            }

            return profile;
        }
        catch (JsonException ex)
        {
            quarantined.Add($"profile {label} 无法读取，已移出：{ex.Message}");
            return null;
        }
    }
}
