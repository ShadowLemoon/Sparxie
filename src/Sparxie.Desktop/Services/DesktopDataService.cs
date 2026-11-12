using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.LauncherCore;

namespace Sparxie.Desktop.Services;

/// <summary>Desktop facade over the config/profile domain (config.json, including the desktop preferences section).</summary>
public sealed class DesktopDataService
{
    private readonly AppConfigStore _configStore;

    public DesktopDataService(string? configPath = null)
    {
        _configStore = new AppConfigStore(configPath ?? Path.Combine(AppDataDirectory.Resolve(), "config.json"));
    }

    public AppConfig Config { get; private set; } = new();

    /// <summary>界面偏好，存放在 config.json 的 desktop 段。</summary>
    public DesktopSettings Preferences => Config.Desktop ??= new DesktopSettings();

    public string ConfigPath => _configStore.ConfigPath;

    public void Load()
    {
        var result = _configStore.Load();
        if (result.State == ConfigLoadState.Failed)
        {
            throw new InvalidOperationException(result.Error?.Message ?? "配置加载失败");
        }

        // what the load repaired, left out or restored (the original is kept beside config.json); the log is the record
        foreach (var change in result.Changes)
        {
            DesktopLog.Write($"配置 {result.State}: {change}");
        }

        Config = result.Config;
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
            DisplayName: ProfileNames.NextDefault(Config.Profiles.Where(item => item.Game == game).Select(item => item.DisplayName)),
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

    public void SavePreferences() => SaveConfig();
}
