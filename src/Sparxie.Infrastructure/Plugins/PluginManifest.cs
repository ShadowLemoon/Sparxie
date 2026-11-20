using System.Text.Json;
using System.Text.Json.Serialization;
using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Plugins;

/// <summary>插件旁挂的静态元数据（plugin.json）。Desktop 与 Broker 只读它，不执行 DLL。</summary>
public sealed class PluginManifest
{
    public const int SupportedAbiMajor = 1;
    public const int MaxManifestBytes = 64 * 1024;
    public const string ModuleFileName = "module.dll";

    private static readonly HashSet<string> KnownCaps = new(StringComparer.Ordinal)
    {
        "createsProcess", "injects", "needsArgs", "hotParams",
    };

    // 禁止作为游戏 EXE 白名单的系统进程名，避免插件把白名单扩成任意进程注入。
    private static readonly HashSet<string> ForbiddenExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "svchost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "conhost.exe",
        "wininit.exe", "services.exe", "lsass.exe", "csrss.exe", "winlogon.exe", "taskmgr.exe", "regedit.exe",
        "mshta.exe", "wscript.exe", "cscript.exe", "msiexec.exe", "dwm.exe",
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int AbiMajor { get; set; }

    public List<string> ExeNames { get; set; } = [];

    public List<string> Caps { get; set; } = [];

    public string? Version { get; set; }

    public string? Homepage { get; set; }

    public string? License { get; set; }

    /// <summary>可选：允许插件通过宿主回调修改的 HKCU 键前缀（由核心备份并恢复，与画面设置无关）。</summary>
    public RegistryHooksSpec? RegistryHooks { get; set; }

    /// <summary>设置 schema（原样保留，由后续的表单与校验使用）。</summary>
    [JsonConverter(typeof(RawJsonConverter))]
    public string? SettingsSchema { get; set; }

    public bool HasCap(string cap) => Caps.Contains(cap, StringComparer.Ordinal);

    /// <summary>解析并校验；失败返回 false 并给出原因。</summary>
    public static bool TryParse(byte[] bytes, string expectedId, out PluginManifest manifest, out string error)
    {
        manifest = new PluginManifest();
        if (bytes.Length > MaxManifestBytes)
        {
            error = "plugin.json 过大";
            return false;
        }

        PluginManifest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PluginManifest>(bytes, ReadOptions);
        }
        catch (JsonException ex)
        {
            error = $"plugin.json 无法解析: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "plugin.json 为空";
            return false;
        }

        if (parsed.Id != expectedId || !GameIds.IsValidId(parsed.Id) || GameIds.TryGetBuiltIn(parsed.Id, out _))
        {
            error = $"插件 id 非法或与目录名不一致: {parsed.Id}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.DisplayName) || parsed.DisplayName.Length > 64)
        {
            error = "displayName 为空或过长";
            return false;
        }

        if (parsed.ExeNames.Count is 0 or > 8)
        {
            error = "exeNames 数量非法";
            return false;
        }

        foreach (var exe in parsed.ExeNames)
        {
            if (!IsPlainExeName(exe) || ForbiddenExeNames.Contains(exe))
            {
                error = $"exeNames 含非法名称: {exe}";
                return false;
            }
        }

        if (parsed.Caps.Any(cap => !KnownCaps.Contains(cap)))
        {
            error = "caps 含未知能力";
            return false;
        }

        if (parsed.RegistryHooks is { } hooks && (hooks.Keys.Count is 0 or > 8 || hooks.Keys.Any(key => !RegistryHookSession.IsValidPrefix(key))))
        {
            error = "registryHooks.keys 为空或含非法前缀";
            return false;
        }

        manifest = parsed;
        error = string.Empty;
        return true;
    }

    private static bool IsPlainExeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(name) == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\', ':']) < 0;

    private sealed class RawJsonConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : JsonDocument.ParseValue(ref reader).RootElement.GetRawText();

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteRawValue(value);
            }
        }
    }
}

public sealed class RegistryHooksSpec
{
    public List<string> Keys { get; set; } = [];
}
