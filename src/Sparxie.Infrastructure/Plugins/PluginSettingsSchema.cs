using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sparxie.Infrastructure.Plugins;

public enum PluginFieldKind
{
    Bool,
    Int,
    Enum,
    String,
}

public sealed record PluginFieldOption(string Value, string Label);

/// <summary>仅当另一个字段等于某值时才显示/生效的条件（字段仍会被归一化和传给插件，只是界面上隐藏）。</summary>
public sealed record PluginFieldCondition(string Key, JsonNode Value);

public sealed record PluginField(
    string Key,
    string Label,
    PluginFieldKind Kind,
    JsonNode Default,
    int Min,
    int Max,
    int Step,
    IReadOnlyList<PluginFieldOption> Options,
    int MaxLength,
    PluginFieldCondition? ShowWhen,
    string? Hint,
    string? Group = null);

/// <summary>
/// 插件设置的声明式 schema（plugin.json 的 settingsSchema）：由核心据此校验、补默认值并生成设置表单，插件自己不提供界面。
/// 形态：<c>{ "fields": [ { "key", "label", "type": "bool|int|enum|string", "default", "min", "max", "step", "options", "maxLength", "showWhen": {"key","equals"}, "hint" } ] }</c>。
/// <see cref="Normalize"/> 是全函数：任何输入都映射成一份合法的设置，不抛异常，也不是拒绝会话的理由。
/// </summary>
public sealed class PluginSettingsSchema
{
    public const int MaxFields = 64;
    public const int MaxSettingsBytes = 16 * 1024;
    private const int MaxStringLength = 256;
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]{0,39}\\z", RegexOptions.CultureInvariant);

    private PluginSettingsSchema(IReadOnlyList<PluginField> fields) => Fields = fields;

    public IReadOnlyList<PluginField> Fields { get; }

    /// <summary>解析并校验 schema 本身；schema 写错会让插件的 manifest 无效，而不是运行时才出错。</summary>
    public static bool TryParse(string? json, out PluginSettingsSchema schema, out string error)
    {
        schema = new PluginSettingsSchema([]);
        if (string.IsNullOrWhiteSpace(json))
        {
            error = string.Empty;
            return true; // 没有 schema = 没有设置项
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("fields", out var fieldsElement)
                || fieldsElement.ValueKind != JsonValueKind.Array)
            {
                error = "settingsSchema 必须是含 fields 数组的对象";
                return false;
            }

            if (fieldsElement.GetArrayLength() > MaxFields)
            {
                error = $"settingsSchema 字段超过 {MaxFields} 个";
                return false;
            }

            var fields = new List<PluginField>();
            foreach (var element in fieldsElement.EnumerateArray())
            {
                if (!TryParseField(element, fields, out var field, out error))
                {
                    return false;
                }

                fields.Add(field!);
            }

            schema = new PluginSettingsSchema(fields);
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"settingsSchema 不是合法 JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>按 schema 归一化：未知键丢弃，类型不对/缺失用默认值，数值夹到范围并对齐步长，枚举不在选项里用默认值。</summary>
    public JsonObject Normalize(JsonObject? input)
    {
        var result = new JsonObject();
        foreach (var field in Fields)
        {
            JsonNode? raw = null;
            input?.TryGetPropertyValue(field.Key, out raw);
            result[field.Key] = NormalizeValue(field, raw);
        }

        return result;
    }

    /// <summary>该字段在给定设置下是否应当显示（<see cref="PluginField.ShowWhen"/>）。</summary>
    public static bool IsVisible(PluginField field, JsonObject values)
    {
        if (field.ShowWhen is not { } condition)
        {
            return true;
        }

        values.TryGetPropertyValue(condition.Key, out var current);
        return current is not null && Equal(current, condition.Value);
    }

    private static JsonNode NormalizeValue(PluginField field, JsonNode? raw)
    {
        switch (field.Kind)
        {
            case PluginFieldKind.Bool:
                return JsonValue.Create(raw is JsonValue boolValue && boolValue.TryGetValue<bool>(out var b) ? b : field.Default.GetValue<bool>())!;

            case PluginFieldKind.Int:
            {
                var value = field.Default.GetValue<int>();
                if (raw is JsonValue number && TryReadInt(number, out var parsed))
                {
                    value = parsed;
                }

                value = Math.Clamp(value, field.Min, field.Max);
                if (field.Step > 1)
                {
                    value = field.Min + (int)Math.Round((value - field.Min) / (double)field.Step, MidpointRounding.AwayFromZero) * field.Step;
                    value = Math.Clamp(value, field.Min, field.Max);
                }

                return JsonValue.Create(value)!;
            }

            case PluginFieldKind.Enum:
            {
                var value = raw is JsonValue text && text.TryGetValue<string>(out var s) && field.Options.Any(option => option.Value == s)
                    ? s
                    : field.Default.GetValue<string>();
                return JsonValue.Create(value)!;
            }

            default:
            {
                var value = raw is JsonValue text && text.TryGetValue<string>(out var s) ? s.Trim() : field.Default.GetValue<string>();
                if (value.Length > field.MaxLength)
                {
                    value = value[..field.MaxLength];
                }

                return JsonValue.Create(string.Concat(value.Where(ch => !char.IsControl(ch))))!;
            }
        }
    }

    private static bool TryReadInt(JsonValue value, out int result)
    {
        result = 0;
        if (value.TryGetValue<int>(out result))
        {
            return true;
        }

        if (value.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            result = (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
            return true;
        }

        return false;
    }

    private static bool TryParseField(JsonElement element, List<PluginField> previous, out PluginField? field, out string error)
    {
        field = null;
        error = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "fields 的元素必须是对象";
            return false;
        }

        string? Text(string name) => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        var key = Text("key");
        if (key is null || !KeyPattern.IsMatch(key) || previous.Any(item => item.Key == key))
        {
            error = $"字段 key 非法或重复: {key}";
            return false;
        }

        var label = Text("label");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 64)
        {
            error = $"字段 {key} 的 label 为空或过长";
            return false;
        }

        var group = Text("group");
        if (group is { Length: > 32 })
        {
            error = $"字段 {key} 的 group 过长";
            return false;
        }

        var hint = Text("hint");
        if (hint is { Length: > 200 })
        {
            error = $"字段 {key} 的 hint 过长";
            return false;
        }

        PluginFieldCondition? showWhen = null;
        if (element.TryGetProperty("showWhen", out var when))
        {
            if (when.ValueKind != JsonValueKind.Object
                || !when.TryGetProperty("key", out var whenKey) || whenKey.ValueKind != JsonValueKind.String
                || !when.TryGetProperty("equals", out var whenEquals)
                || previous.All(item => item.Key != whenKey.GetString()))
            {
                error = $"字段 {key} 的 showWhen 非法（必须引用前面已声明的字段）";
                return false;
            }

            showWhen = new PluginFieldCondition(whenKey.GetString()!, JsonNode.Parse(whenEquals.GetRawText())!);
        }

        var type = Text("type");
        element.TryGetProperty("default", out var defaultElement);
        switch (type)
        {
            case "bool":
                if (defaultElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = $"字段 {key} 的 default 必须是 bool";
                    return false;
                }

                field = new PluginField(key, label, PluginFieldKind.Bool, JsonValue.Create(defaultElement.GetBoolean())!, 0, 0, 1, [], 0, showWhen, hint, group);
                return true;

            case "int":
            {
                if (!element.TryGetProperty("min", out var min) || !min.TryGetInt32(out var minValue)
                    || !element.TryGetProperty("max", out var max) || !max.TryGetInt32(out var maxValue)
                    || minValue > maxValue
                    || defaultElement.ValueKind != JsonValueKind.Number || !defaultElement.TryGetInt32(out var defaultValue)
                    || defaultValue < minValue || defaultValue > maxValue)
                {
                    error = $"字段 {key} 的 min/max/default 非法";
                    return false;
                }

                var step = 1;
                if (element.TryGetProperty("step", out var stepElement) && (!stepElement.TryGetInt32(out step) || step < 1 || step > maxValue - minValue + 1))
                {
                    error = $"字段 {key} 的 step 非法";
                    return false;
                }

                field = new PluginField(key, label, PluginFieldKind.Int, JsonValue.Create(defaultValue)!, minValue, maxValue, step, [], 0, showWhen, hint, group);
                return true;
            }

            case "enum":
            {
                if (!element.TryGetProperty("options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array
                    || optionsElement.GetArrayLength() is 0 or > 32)
                {
                    error = $"字段 {key} 的 options 数量非法";
                    return false;
                }

                var options = new List<PluginFieldOption>();
                foreach (var option in optionsElement.EnumerateArray())
                {
                    var value = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var optionLabel = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                    if (string.IsNullOrEmpty(value) || value.Length > 64 || string.IsNullOrWhiteSpace(optionLabel) || optionLabel.Length > 64 || options.Any(o => o.Value == value))
                    {
                        error = $"字段 {key} 的选项非法或重复";
                        return false;
                    }

                    options.Add(new PluginFieldOption(value, optionLabel));
                }

                if (defaultElement.ValueKind != JsonValueKind.String || options.All(o => o.Value != defaultElement.GetString()))
                {
                    error = $"字段 {key} 的 default 必须是某个选项的 value";
                    return false;
                }

                field = new PluginField(key, label, PluginFieldKind.Enum, JsonValue.Create(defaultElement.GetString()!)!, 0, 0, 1, options, 0, showWhen, hint, group);
                return true;
            }

            case "string":
            {
                var maxLength = MaxStringLength;
                if (element.TryGetProperty("maxLength", out var maxLengthElement) && (!maxLengthElement.TryGetInt32(out maxLength) || maxLength < 1 || maxLength > MaxStringLength))
                {
                    error = $"字段 {key} 的 maxLength 非法";
                    return false;
                }

                if (defaultElement.ValueKind != JsonValueKind.String || defaultElement.GetString()!.Length > maxLength)
                {
                    error = $"字段 {key} 的 default 必须是不超过 maxLength 的字符串";
                    return false;
                }

                field = new PluginField(key, label, PluginFieldKind.String, JsonValue.Create(defaultElement.GetString()!)!, 0, 0, 1, [], maxLength, showWhen, hint, group);
                return true;
            }

            default:
                error = $"字段 {key} 的 type 非法: {type}";
                return false;
        }
    }

    private static bool Equal(JsonNode a, JsonNode b) =>
        a is JsonValue left && b is JsonValue right
        && (left.TryGetValue<bool>(out var lb) && right.TryGetValue<bool>(out var rb) ? lb == rb
            : left.TryGetValue<string>(out var ls) && right.TryGetValue<string>(out var rs) ? ls == rs
            : left.TryGetValue<double>(out var ld) && right.TryGetValue<double>(out var rd) && ld.Equals(rd));

    /// <summary>设置对象转紧凑 JSON 文本（传给插件的 settings_json）。</summary>
    public static string ToJson(JsonObject settings) => settings.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>把 settings_json 文本解析成对象；空、非对象或损坏一律当作"没有设置"。</summary>
    public static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxSettingsBytes)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
