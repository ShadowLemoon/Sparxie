using System.Text.Json;

namespace Sparxie.Desktop.Services;

/// <summary>GUI-only preferences. Game/Profile data remains in config.json.</summary>
public sealed class DesktopPreferences
{
    public string Theme { get; set; } = "System";

    /// <summary>旧版开关“退出前确认”，保留用于迁移：CloseAction 缺省时 true→询问，false→直接退出。</summary>
    public bool ConfirmBeforeExit { get; set; } = true;

    /// <summary>点击窗口关闭按钮时：Ask 退出前确认 / Tray 最小化到系统托盘 / Exit 直接退出。</summary>
    public string? CloseAction { get; set; }

    public CloseBehavior ResolveCloseBehavior() =>
        Enum.TryParse<CloseBehavior>(CloseAction, out var parsed)
            ? parsed
            : (ConfirmBeforeExit ? CloseBehavior.Ask : CloseBehavior.Exit);

    /// <summary>调试模式：主页显示启动/注入状态条等诊断信息。默认关闭；环境变量 SPARXIE_DEBUG=1 可强制开启。</summary>
    public bool DebugMode { get; set; }

    /// <summary>主页最后选中的游戏标签，跨启动持久化。</summary>
    public string? SelectedGame { get; set; }

    /// <summary>每个游戏上次使用的配置 ID（键为 GameType 名）。点游戏标题时据此恢复，保证结果可预期。</summary>
    public Dictionary<string, string> LastProfileByGame { get; set; } = new();
}

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public DesktopPreferencesStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                var fresh = new DesktopPreferences();
                Save(fresh);
                return fresh;
            }

            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(Path), JsonOptions)
                ?? new DesktopPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A read-only install directory should not prevent the desktop shell from opening.
            return new DesktopPreferences();
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, JsonSerializer.Serialize(preferences, JsonOptions));
    }
}

public enum CloseBehavior
{
    /// <summary>退出前弹确认。</summary>
    Ask,

    /// <summary>关闭窗口时最小化到系统托盘，托盘菜单里退出。</summary>
    Tray,

    /// <summary>直接退出。</summary>
    Exit,
}
