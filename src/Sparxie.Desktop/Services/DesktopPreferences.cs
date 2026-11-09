using System.Text.Json;

namespace Sparxie.Desktop.Services;

/// <summary>GUI-only preferences. Game/Profile data remains in config.json.</summary>
public sealed class DesktopPreferences
{
    public string Theme { get; set; } = "System";

    public bool ConfirmBeforeExit { get; set; } = true;

    /// <summary>主页最后选中的游戏标签，跨启动持久化。</summary>
    public string? SelectedGame { get; set; }
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
