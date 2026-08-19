using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.LauncherCore;

/// <summary>
/// 供 CLI 和未来图形宿主共用的 Profile 业务规则。
/// 这里只修改已加载的配置对象；宿主负责调用 AppConfigStore 进行原子保存。
/// </summary>
public static class ProfileManager
{
    public static GameProfile Add(AppConfig config, ProfileMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(mutation);

        var id = RequireText(mutation.Id, "Profile ID");
        if (config.Profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.Ordinal)))
        {
            throw Fail($"Profile ID 已存在: {id}");
        }

        if (mutation.Game is not { } game)
        {
            throw Fail("创建 Profile 必须指定游戏类型");
        }

        var profile = new GameProfile
        {
            Id = id,
            DisplayName = RequireText(mutation.DisplayName, "Profile 名称"),
            Game = game,
            ExecutablePath = NormalizeExecutablePath(game, mutation.ExecutablePath),
            Hoyo = game == GameType.ZenlessZoneZero ? null : new HoyoProfileSettings(),
        };

        ApplySettings(profile, mutation);
        ValidateCompleteProfile(profile);

        config.Profiles.Add(profile);
        if (config.Profiles.Count == 1)
        {
            config.SelectedProfileId = profile.Id;
        }

        return profile;
    }

    public static GameProfile Update(AppConfig config, string selector, ProfileMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(mutation);

        if (!mutation.HasAnySettableField)
        {
            throw Fail("至少需要一个可修改字段");
        }

        var current = ProfileSelector.Select(config, selector);
        var updated = Clone(current);

        if (mutation.DisplayName is not null)
        {
            updated.DisplayName = RequireText(mutation.DisplayName, "Profile 名称");
        }

        if (mutation.ExecutablePath is not null)
        {
            updated.ExecutablePath = NormalizeExecutablePath(updated.Game, mutation.ExecutablePath);
        }

        ApplySettings(updated, mutation);
        ValidateUpdatedProfile(updated);

        var index = config.Profiles.IndexOf(current);
        config.Profiles[index] = updated;
        return updated;
    }

    public static GameProfile Select(AppConfig config, string selector)
    {
        ArgumentNullException.ThrowIfNull(config);

        var profile = ProfileSelector.Select(config, selector);
        config.SelectedProfileId = profile.Id;
        return profile;
    }

    public static GameProfile Remove(AppConfig config, string selector)
    {
        ArgumentNullException.ThrowIfNull(config);

        var profile = ProfileSelector.Select(config, selector);
        if (!config.Profiles.Remove(profile))
        {
            throw Fail($"无法删除 Profile: {profile.Id}");
        }

        if (string.Equals(config.SelectedProfileId, profile.Id, StringComparison.Ordinal)
            || !config.Profiles.Any(item => string.Equals(item.Id, config.SelectedProfileId, StringComparison.Ordinal)))
        {
            config.SelectedProfileId = config.Profiles.FirstOrDefault()?.Id;
        }

        return profile;
    }

    private static void ApplySettings(GameProfile profile, ProfileMutation mutation)
    {
        if (mutation.HasHoyoSettings)
        {
            if (profile.Game == GameType.ZenlessZoneZero)
            {
                throw Fail("绝区零 Profile 不支持 Hoyo FPS 或进程优先级设置");
            }

            profile.Hoyo ??= new HoyoProfileSettings();
            var hoyo = profile.Hoyo;
            if (mutation.FpsUnlockEnabled is { } fpsUnlockEnabled)
            {
                hoyo.FpsUnlockEnabled = fpsUnlockEnabled;
            }

            if (mutation.TargetFps is { } targetFps)
            {
                hoyo.TargetFps = targetFps;
            }

            if (mutation.BackgroundFpsLimitEnabled is { } backgroundFpsLimitEnabled)
            {
                hoyo.BackgroundFpsLimitEnabled = backgroundFpsLimitEnabled;
            }

            if (mutation.BackgroundFps is { } backgroundFps)
            {
                hoyo.BackgroundFps = backgroundFps;
            }

            if (mutation.ProcessPriority is { } priority)
            {
                hoyo.ProcessPriority = priority;
            }
        }

        if (mutation.HasGenshinSettings)
        {
            if (profile.Game != GameType.Genshin)
            {
                throw Fail("原神专属设置只能用于原神 Profile");
            }

            profile.Hoyo ??= new HoyoProfileSettings();
            var hoyo = profile.Hoyo;
            if (mutation.GenshinFollowInGamePreset is { } followInGamePreset)
            {
                hoyo.GenshinFollowInGamePreset = followInGamePreset;
            }

            if (mutation.GenshinPreset30Fps is { } preset30Fps)
            {
                hoyo.GenshinPreset30Fps = preset30Fps;
            }

            if (mutation.GenshinPreset60Fps is { } preset60Fps)
            {
                hoyo.GenshinPreset60Fps = preset60Fps;
            }

            if (mutation.GenshinTouchUiScaleOverrideEnabled is { } touchUiScaleOverrideEnabled)
            {
                hoyo.GenshinTouchUiScaleOverrideEnabled = touchUiScaleOverrideEnabled;
            }

            if (mutation.GenshinTouchUiScalePercent is { } touchUiScalePercent)
            {
                hoyo.GenshinTouchUiScalePercent = touchUiScalePercent;
            }
        }
    }

    private static void ValidateCompleteProfile(GameProfile profile)
    {
        var errors = AppConfigValidator.Validate(new AppConfig { Profiles = [profile] });
        if (errors.Count > 0)
        {
            throw Fail(string.Join("；", errors));
        }
    }

    private static void ValidateUpdatedProfile(GameProfile profile)
    {
        // 历史空路径 Profile 仍可编辑非路径字段，兼容此前“先创建、后填路径”的配置契约；
        // 一旦 CLI 传入 --exe，则 NormalizeExecutablePath 已强制完整路径和白名单。
        var errors = AppConfigValidator.Validate(new AppConfig { Profiles = [profile] });
        if (errors.Count > 0)
        {
            throw Fail(string.Join("；", errors));
        }
    }

    private static string NormalizeExecutablePath(GameType game, string? executablePath)
    {
        var input = RequireText(executablePath, "EXE 路径");
        if (!Path.IsPathFullyQualified(input))
        {
            throw Fail("EXE 路径必须是完整路径");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(input);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(LauncherFailureKind.ProfileManagement, "EXE 路径无效", ex);
        }

        if (!GameExecutables.IsAllowed(game, fullPath))
        {
            throw Fail($"EXE 名称不属于 {FormatGame(game)} 白名单: {Path.GetFileName(fullPath)}");
        }

        return fullPath;
    }

    private static GameProfile Clone(GameProfile source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Game = source.Game,
        ExecutablePath = source.ExecutablePath,
        Hoyo = source.Hoyo is null
            ? null
            : new HoyoProfileSettings
            {
                FpsUnlockEnabled = source.Hoyo.FpsUnlockEnabled,
                TargetFps = source.Hoyo.TargetFps,
                BackgroundFpsLimitEnabled = source.Hoyo.BackgroundFpsLimitEnabled,
                BackgroundFps = source.Hoyo.BackgroundFps,
                ProcessPriority = source.Hoyo.ProcessPriority,
                GenshinFollowInGamePreset = source.Hoyo.GenshinFollowInGamePreset,
                GenshinPreset30Fps = source.Hoyo.GenshinPreset30Fps,
                GenshinPreset60Fps = source.Hoyo.GenshinPreset60Fps,
                GenshinTouchUiScaleOverrideEnabled = source.Hoyo.GenshinTouchUiScaleOverrideEnabled,
                GenshinTouchUiScalePercent = source.Hoyo.GenshinTouchUiScalePercent,
            },
    };

    private static string RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Fail($"{field} 不能为空");
        }

        return value.Trim();
    }

    private static string FormatGame(GameType game) => game switch
    {
        GameType.Genshin => "genshin",
        GameType.StarRail => "starRail",
        GameType.ZenlessZoneZero => "zenlessZoneZero",
        _ => game.ToString(),
    };

    private static LauncherException Fail(string message) =>
        new(LauncherFailureKind.ProfileManagement, message);
}
