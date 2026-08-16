using Sparxie.Contracts.Models;

namespace Sparxie.LauncherCore;

/// <summary>按 ID/显示名选择启动 Profile，集中处理缺省与歧义语义。</summary>
public static class ProfileSelector
{
    public static GameProfile Select(AppConfig config, string? selector = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Profiles.Count == 0)
        {
            throw new LauncherException(
                LauncherFailureKind.ProfileSelection,
                "配置中没有 Profile");
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            if (!string.IsNullOrWhiteSpace(config.SelectedProfileId))
            {
                var selected = config.Profiles.FirstOrDefault(
                    profile => string.Equals(profile.Id, config.SelectedProfileId, StringComparison.Ordinal));
                if (selected is not null)
                {
                    return selected;
                }
            }

            return config.Profiles[0];
        }

        var byId = config.Profiles
            .Where(profile => string.Equals(profile.Id, selector, StringComparison.Ordinal))
            .ToArray();
        if (byId.Length == 1)
        {
            return byId[0];
        }

        var byName = config.Profiles
            .Where(profile => string.Equals(profile.DisplayName, selector, StringComparison.Ordinal))
            .ToArray();
        if (byName.Length == 1)
        {
            return byName[0];
        }

        if (byName.Length > 1)
        {
            throw new LauncherException(
                LauncherFailureKind.ProfileSelection,
                $"Profile 名称不唯一: {selector}");
        }

        throw new LauncherException(
            LauncherFailureKind.ProfileSelection,
            $"找不到 Profile: {selector}");
    }
}
