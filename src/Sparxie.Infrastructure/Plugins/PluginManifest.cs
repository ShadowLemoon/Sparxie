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

    /// <summary>可选：插件目录里的图标图片文件名（只用于游戏标签/标题，不参与信任哈希；见 <see cref="PluginAssets"/>）。</summary>
    public string? Icon { get; set; }

    /// <summary>可选：插件目录里的主页背景图片文件名。</summary>
    public string? Background { get; set; }

    /// <summary>可选：背景图片的 https 地址（由宿主下载并缓存；<see cref="Background"/> 文件优先）。</summary>
    public string? BackgroundUrl { get; set; }

    /// <summary>可选：返回 JSON 的接口，宿主按 path 取出背景图片地址（<see cref="BackgroundUrl"/> 优先）。</summary>
    public BackgroundApiSpec? BackgroundApi { get; set; }

    /// <summary>可选：作者自述（显示时标注“作者自述，未经验证”）。</summary>
    public string? Author { get; set; }

    public string? Description { get; set; }

    /// <summary>可选：作者对数据/联网行为的说明。</summary>
    public string? PrivacyNote { get; set; }

    /// <summary>可选：相关链接（https，最多 4 项）。</summary>
    public List<PluginLink>? Links { get; set; }

    /// <summary>可选：自动查找 EXE 的提示。</summary>
    public AutoFindSpec? AutoFind { get; set; }

    /// <summary>设置 schema（原样保留，由后续的表单与校验使用）。</summary>
    [JsonConverter(typeof(RawJsonConverter))]
    public string? SettingsSchema { get; set; }

    /// <summary>解析后的设置 schema（<see cref="TryParse"/> 已校验；没有 schema 时为空 schema）。</summary>
    [JsonIgnore]
    public PluginSettingsSchema Schema { get; private set; } = null!;

    public bool HasCap(string cap) => Caps.Contains(cap, StringComparer.Ordinal);

    /// <summary>解析并校验；失败返回 false 并给出原因。</summary>
    public static bool TryParse(byte[] bytes, string expectedId, out PluginManifest manifest, out string error)
    {
        try
        {
            return TryParseCore(bytes, expectedId, out manifest, out error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 不兼容 / 畸形的 plugin.json 只让这个插件不可用，不能让扫描（主程序启动、Broker）抛出去。
            manifest = new PluginManifest();
            error = $"plugin.json 不兼容: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseCore(byte[] bytes, string expectedId, out PluginManifest manifest, out string error)
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

        parsed.ExeNames ??= [];
        parsed.Caps ??= [];
        if (parsed.AutoFind is not null)
        {
            parsed.AutoFind.UninstallNames ??= [];
            parsed.AutoFind.Registry ??= [];
        }

        if (parsed.RegistryHooks is not null)
        {
            parsed.RegistryHooks.Keys ??= [];
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

        if (parsed.Author?.Length > 64 || parsed.Description?.Length > 300 || parsed.PrivacyNote?.Length > 300)
        {
            error = "author（≤64）/ description（≤300）/ privacyNote（≤300）过长";
            return false;
        }

        if (parsed.Links is { } links
            && (links.Count > 4 || links.Any(link => link is null || string.IsNullOrWhiteSpace(link.Title) || link.Title.Length > 32 || !RemoteUrlPolicy.IsAllowed(link.Url))))
        {
            error = "links 最多 4 项，每项需要 title（≤32）和 https 的 url";
            return false;
        }

        if ((parsed.Icon is not null && !PluginAssets.IsValidFileName(parsed.Icon))
            || (parsed.Background is not null && !PluginAssets.IsValidFileName(parsed.Background)))
        {
            error = "icon / background 必须是插件目录里的 png/jpg/webp/bmp 文件名";
            return false;
        }

        if (parsed.AutoFind is { } find
            && (find.UninstallNames.Count > 8 || find.UninstallNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length is < 2 or > 64)))
        {
            error = "autoFind.uninstallNames 最多 8 项，每项 2-64 个字符";
            return false;
        }

        if (parsed.AutoFind is { } registry && (registry.Registry.Count > 8 || !registry.Registry.All(RegistryLookupSpec.IsValid)))
        {
            error = "autoFind.registry 最多 8 项；hive 只能是 HKCU / HKLM，key 与 value 不能为空，不能含 ..";
            return false;
        }

        if (parsed.BackgroundUrl is not null && !RemoteUrlPolicy.IsAllowed(parsed.BackgroundUrl))
        {
            error = "backgroundUrl 必须是 https 地址，且不能指向本机或内网";
            return false;
        }

        if (parsed.BackgroundApi is { } api
            && (!RemoteUrlPolicy.IsAllowed(api.Url) || string.IsNullOrWhiteSpace(api.Path) || api.Path.Length > 128
                || api.ItemPath?.Length > 128 || api.VideoPath?.Length > 128
                || (api.Method is not null && api.Method is not ("GET" or "POST"))
                || (api.Body is not null && (api.Body.Length > 4096 || !IsJsonObject(api.Body)))))
        {
            error = "backgroundApi 需要 https 的 url 和不超过 128 字符的 path（如 data.0.url）";
            return false;
        }

        if (!PluginSettingsSchema.TryParse(parsed.SettingsSchema, out var schema, out var schemaError))
        {
            error = schemaError;
            return false;
        }

        parsed.Schema = schema;
        manifest = parsed;
        error = string.Empty;
        return true;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPlainExeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(name) == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\', ':']) < 0;

    internal sealed class RawJsonConverter : JsonConverter<string?>
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

public sealed class PluginLink
{
    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

public sealed class AutoFindSpec
{
    /// <summary>卸载项（注册表 Uninstall 下的键名）里包含的关键字；命中的安装目录下会搜索白名单 EXE。</summary>
    public List<string> UninstallNames { get; set; } = [];

    /// <summary>注册表查找项：读到的值是安装目录（或 EXE 路径），宿主在其下找白名单 EXE。</summary>
    public List<RegistryLookupSpec> Registry { get; set; } = [];
}

public sealed class RegistryHooksSpec
{
    public List<string> Keys { get; set; } = [];
}
