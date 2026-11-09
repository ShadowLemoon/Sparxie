using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.LauncherCore;

namespace Sparxie.Desktop.Services;

/// <summary>Desktop facade over the same config/profile domain used by the CLI.</summary>
public sealed class DesktopDataService
{
    private readonly AppConfigStore _configStore;
    private readonly DesktopPreferencesStore _preferencesStore;

    public DesktopDataService(string? configPath = null, string? preferencesPath = null)
    {
        _configStore = new AppConfigStore(configPath ?? Path.Combine(AppDataDirectory.Resolve(), "config.json"));
        _preferencesStore = new DesktopPreferencesStore(
            preferencesPath ?? Path.Combine(AppDataDirectory.Resolve(), "desktop.json"));
    }

    public AppConfig Config { get; private set; } = new();

    public DesktopPreferences Preferences { get; private set; } = new();

    public string ConfigPath => _configStore.ConfigPath;

    public string PreferencesPath => _preferencesStore.Path;

    public void Load()
    {
        var result = _configStore.Load();
        if (result.State == ConfigLoadState.Failed)
        {
            throw new InvalidOperationException(result.Error?.Message ?? "配置加载失败");
        }

        Config = result.Config;
        Preferences = _preferencesStore.Load();
    }

    public GameProfile? TryGetSelectedProfile()
    {
        if (Config.Profiles.Count == 0)
        {
            return null;
        }

        try
        {
            return ProfileSelector.Select(Config);
        }
        catch (LauncherException)
        {
            return Config.Profiles[0];
        }
    }

    /// <summary>没有任一 Profile 时创建占位 Profile；空路径由配置契约允许，启动时再由会话边界拒绝。</summary>
    public GameProfile EnsureDefaultProfile(GameType game)
    {
        if (Config.Profiles.Count > 0)
        {
            return Config.Profiles[0];
        }

        return AddProfile(new ProfileMutation(
            Id: "default",
            DisplayName: "默认配置",
            Game: game));
    }

    public void SelectProfile(string selector)
    {
        ProfileManager.Select(Config, selector);
        SaveConfig();
    }

    public GameProfile AddProfile(ProfileMutation mutation)
    {
        var profile = ProfileManager.Add(Config, mutation);
        SaveConfig();
        return profile;
    }

    public GameProfile UpdateProfile(string selector, ProfileMutation mutation)
    {
        var profile = ProfileManager.Update(Config, selector, mutation);
        SaveConfig();
        return profile;
    }

    public void RemoveProfile(string selector)
    {
        ProfileManager.Remove(Config, selector);
        SaveConfig();
    }

    public void SaveConfig() => _configStore.Save(Config);

    public void SavePreferences() => _preferencesStore.Save(Preferences);
}
