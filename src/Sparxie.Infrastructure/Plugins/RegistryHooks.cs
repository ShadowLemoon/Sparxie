using System.Text.Json;
using Microsoft.Win32;
using Sparxie.Infrastructure.Configuration;

namespace Sparxie.Infrastructure.Plugins;

/// <summary>注册表里的一个值（只支持 DWORD 与字符串）。<see cref="Data"/>：DWORD 为十进制文本，字符串为原文。</summary>
public sealed record RegistryHookValue(string Kind, string Data)
{
    public const string Dword = "dword";
    public const string String = "string";
}

/// <summary>HKCU 下的值读写。抽象出来是为了测试，也让备份组件不依赖具体注册表实现。</summary>
public interface IRegistryHookStore
{
    RegistryHookValue? Read(string key, string name);

    void Write(string key, string name, RegistryHookValue value);

    void Delete(string key, string name);
}

/// <summary>真实的 HKCU 实现。只处理 DWORD 和字符串，其他类型视为"无法安全备份"而抛异常，宁可拒绝也不丢信息。</summary>
public sealed class CurrentUserRegistryHookStore : IRegistryHookStore
{
    public RegistryHookValue? Read(string key, string name)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key, writable: false);
        if (subKey is null || subKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return null;
        }

        return subKey.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegistryHookValue(RegistryHookValue.Dword, unchecked((uint)(int)value).ToString()),
            RegistryValueKind.String => new RegistryHookValue(RegistryHookValue.String, (string)value),
            var other => throw new NotSupportedException($"无法备份类型为 {other} 的注册表值"),
        };
    }

    public void Write(string key, string name, RegistryHookValue value)
    {
        using var subKey = Registry.CurrentUser.CreateSubKey(key, writable: true);
        if (value.Kind == RegistryHookValue.Dword)
        {
            subKey.SetValue(name, unchecked((int)uint.Parse(value.Data)), RegistryValueKind.DWord);
        }
        else
        {
            subKey.SetValue(name, value.Data, RegistryValueKind.String);
        }
    }

    public void Delete(string key, string name)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key, writable: true);
        subKey?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// 插件注册表键的备份/恢复钩子。与画面设置（<c>GameRegistrySettings</c>）完全无关、互不共用代码或备份文件。
/// 插件只能通过宿主回调改键：key 必须落在 plugin.json 声明的 HKCU 前缀内；某个值第一次被改动之前，
/// 原值（含"原本不存在"）先落盘，再写入；<see cref="Restore"/> 按相反顺序还原。
/// 备份文件自带允许的前缀，所以插件已被删除时仍可恢复（<see cref="RestoreLeftover"/>）。
/// </summary>
public sealed class RegistryHookSession
{
    public const int MaxKeyLength = 255;
    public const int MaxNameLength = 255;
    public const int MaxStringLength = 4096;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // 不允许作为声明前缀的路径段：自启动、策略、登录、系统类注册等位置。
    private static readonly HashSet<string> ForbiddenSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run", "RunOnce", "RunOnceEx", "RunServices", "RunServicesOnce", "Policies", "Winlogon",
        "Image File Execution Options", "Classes", "Wow6432Node", "Microsoft",
    };

    private readonly IRegistryHookStore _store;
    private readonly string _backupPath;
    private readonly string[] _prefixes;
    private readonly string _pluginId;
    private readonly List<Entry> _entries = [];
    private readonly object _gate = new();

    public RegistryHookSession(string pluginId, IEnumerable<string> allowedPrefixes, IRegistryHookStore store, string backupPath)
    {
        _pluginId = pluginId;
        _prefixes = allowedPrefixes.Select(Normalize).ToArray();
        _store = store;
        _backupPath = backupPath;
        if (_prefixes.Length == 0 || _prefixes.Any(prefix => !IsValidPrefix(prefix)))
        {
            throw new ArgumentException("registryHooks.keys 为空或含非法前缀", nameof(allowedPrefixes));
        }
    }

    public static string BackupPathFor(string dataRoot, string pluginId) =>
        Path.Combine(dataRoot, "recovery", $"registry-hook-{pluginId}.json");

    public bool HasPendingBackup => _entries.Count > 0;

    /// <summary>manifest 里声明的前缀必须形如 <c>Software\Vendor\Game</c>：至少三段，且不能落在自启动/策略/系统类位置。</summary>
    public static bool IsValidPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > MaxKeyLength || prefix != prefix.Trim())
        {
            return false;
        }

        var segments = prefix.Split('\\');
        return segments.Length >= 3
            && segments[0].Equals("Software", StringComparison.OrdinalIgnoreCase)
            && segments.All(IsValidSegment)
            && !segments.Skip(1).Any(segment => ForbiddenSegments.Contains(segment));
    }

    public (bool Ok, string Message) SetDword(string key, string name, uint value) =>
        Apply(key, name, new RegistryHookValue(RegistryHookValue.Dword, value.ToString()));

    public (bool Ok, string Message) SetString(string key, string name, string value) =>
        value.Length > MaxStringLength
            ? (false, "字符串值过长")
            : Apply(key, name, new RegistryHookValue(RegistryHookValue.String, value));

    public (bool Ok, string Message) DeleteValue(string key, string name) => Apply(key, name, null);

    /// <summary>把本会话改动过的值恢复为原样。全部成功才删除备份文件；有失败就保留，下次启动再试。</summary>
    public bool Restore()
    {
        lock (_gate)
        {
            var ok = RestoreEntries(_store, _entries, _backupPath, remove: true);
            if (!ok && _entries.Count > 0)
            {
                // 已经恢复的条目要从备份里去掉，否则下次启动会把它们的旧原值再写一遍，覆盖用户期间的修改。
                try
                {
                    Persist();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 写不回去就保留原备份：宁可重放旧原值也不丢备份
                }
            }

            return ok;
        }
    }

    /// <summary>
    /// 启动前处理上次异常退出遗留的备份（无需插件仍然存在）。条目会再次按备份里记录的前缀校验，
    /// 篡改过的备份文件不能借此写入声明范围之外的键。
    /// </summary>
    public static bool RestoreLeftover(string backupPath, IRegistryHookStore store)
    {
        if (!File.Exists(backupPath))
        {
            return true;
        }

        BackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(backupPath), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false; // 读不懂的备份不删，留给人工处理
        }

        if (file?.Prefixes is not { Count: > 0 } prefixes || file.Entries is null || prefixes.Any(prefix => !IsValidPrefix(prefix)))
        {
            return false;
        }

        var safe = file.Entries.Where(entry => IsInside(prefixes, entry.Key) && IsValidName(entry.Name)).ToList();
        var allSafe = safe.Count == file.Entries.Count; // RestoreEntries 会从 safe 里移除已恢复的条目，先记下
        var rejected = file.Entries.Where(entry => !safe.Contains(entry)).ToList(); // 先记下前缀之外的条目
        var ok = RestoreEntries(store, safe, backupPath, remove: allSafe);

        // 部分恢复：把已经恢复的条目从备份里去掉（只剩没恢复的和被拒绝的），避免下次重放旧原值覆盖用户的修改。
        if (!(ok && allSafe) && safe.Count + rejected.Count < file.Entries.Count)
        {
            try
            {
                AtomicFile.WriteAllTextAtomic(backupPath, JsonSerializer.Serialize(
                    new BackupFile(file.PluginId, prefixes, [.. safe, .. rejected]), Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不回去就保留原备份
            }
        }

        return ok && allSafe;
    }

    private (bool Ok, string Message) Apply(string key, string name, RegistryHookValue? value)
    {
        key = Normalize(key);
        if (!IsInside(_prefixes, key))
        {
            return (false, "注册表键不在 plugin.json 声明的前缀内");
        }

        if (!IsValidName(name))
        {
            return (false, "注册表值名非法");
        }

        lock (_gate)
        {
            try
            {
                if (!_entries.Any(entry => Same(entry, key, name)))
                {
                    var original = _store.Read(key, name);
                    var added = new Entry(key, name, original is not null, original?.Kind, original?.Data);
                    _entries.Add(added);
                    try
                    {
                        Persist(); // 先落盘原值，再写入：中途崩溃也能恢复
                    }
                    catch
                    {
                        _entries.Remove(added); // 没落盘就不能算"已备份"，否则重试会跳过备份直接写
                        throw;
                    }
                }

                if (value is null)
                {
                    _store.Delete(key, name);
                }
                else
                {
                    _store.Write(key, name, value);
                }

                return (true, string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return (false, ex.Message);
            }
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        AtomicFile.WriteAllTextAtomic(_backupPath, JsonSerializer.Serialize(new BackupFile(_pluginId, [.. _prefixes], [.. _entries]), Json));
    }

    private static bool RestoreEntries(IRegistryHookStore store, List<Entry> entries, string backupPath, bool remove)
    {
        var ok = true;
        foreach (var entry in Enumerable.Reverse(entries).ToList())
        {
            try
            {
                if (entry.Existed && entry.Kind is not null && entry.Data is not null)
                {
                    store.Write(entry.Key, entry.Name, new RegistryHookValue(entry.Kind, entry.Data));
                }
                else
                {
                    store.Delete(entry.Key, entry.Name);
                }

                entries.Remove(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or FormatException)
            {
                ok = false;
            }
        }

        if (ok && remove)
        {
            try
            {
                File.Delete(backupPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }

        return ok;
    }

    private static string Normalize(string key) => key.Trim().Trim('\\');

    private static bool IsValidSegment(string segment) =>
        segment.Length is > 0 and <= 128
        && segment != "." && segment != ".."
        && segment.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0;

    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name.IndexOf('\0') < 0 && name.IndexOf('\\') < 0;

    private static bool IsInside(IEnumerable<string> prefixes, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || !key.Split('\\').All(IsValidSegment))
        {
            return false;
        }

        return prefixes.Any(prefix =>
            key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Same(Entry entry, string key, string name) =>
        entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    public sealed record Entry(string Key, string Name, bool Existed, string? Kind, string? Data);

    public sealed record BackupFile(string PluginId, List<string> Prefixes, List<Entry> Entries);
}
