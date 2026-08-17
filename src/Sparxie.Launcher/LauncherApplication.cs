using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.LauncherCore;

namespace Sparxie.Launcher;

public static class LauncherApplication
{
    public static Task<int> RunAsync(string[] args)
    {
        return RunAsync(args, Console.In, Console.Out, Console.Error);
    }

    public static Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        return RunAsync(args, input, output, error, configPath: null);
    }

    /// <summary>由控制台宿主调用；configPath 仅供自动化测试指定隔离配置路径。</summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string? configPath)
    {
        var parsed = LauncherCommandParser.Parse(args);
        if (!parsed.Success || parsed.Command is null)
        {
            error.WriteLine(parsed.Error);
            PrintUsage(error);
            return 2;
        }

        if (parsed.Command.Kind == LauncherCommandKind.Help)
        {
            PrintUsage(output);
            return 0;
        }

        var resolvedConfigPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        var store = new AppConfigStore(resolvedConfigPath);
        var loadResult = store.Load();
        if (loadResult.State == ConfigLoadState.Failed)
        {
            error.WriteLine(loadResult.Error?.Message ?? "配置加载失败");
            return 3;
        }

        if (loadResult.State == ConfigLoadState.RestoredFromCorrupt)
        {
            output.WriteLine($"警告：旧配置异常，已备份为 {Path.GetFileName(loadResult.BackupPath)}，当前使用空白配置。");
        }

        if (parsed.Command.Kind != LauncherCommandKind.Launch)
        {
            return ExecuteProfileCommand(parsed.Command, loadResult.Config, store, output, error);
        }

        GameProfile profile;
        try
        {
            profile = ProfileSelector.Select(loadResult.Config, parsed.Command.ProfileSelector);
        }
        catch (LauncherException ex)
        {
            error.WriteLine(ex.Message);
            return 4;
        }

        output.WriteLine($"准备启动：{profile.DisplayName} ({FormatGame(profile.Game)})");
        try
        {
            await using var launcher = new LauncherClient(new BrokerClient());
            var session = await launcher.StartSessionAsync(profile).ConfigureAwait(false);
            return await MonitorSessionAsync(session, input, output, error).ConfigureAwait(false);
        }
        catch (LauncherException ex)
        {
            error.WriteLine(ex.Message);
            return ex.Kind == LauncherFailureKind.BrokerRejected ? 6 : 5;
        }
        catch (Exception ex)
        {
            error.WriteLine($"启动器异常：{ex.Message}");
            return 5;
        }
    }

    private static int ExecuteProfileCommand(
        LauncherCommand command,
        AppConfig config,
        AppConfigStore store,
        TextWriter output,
        TextWriter error)
    {
        try
        {
            switch (command.Kind)
            {
                case LauncherCommandKind.List:
                    WriteProfiles(config, output);
                    return 0;

                case LauncherCommandKind.ProfileShow:
                    WriteProfile(ProfileSelector.Select(config, command.ProfileSelector), output);
                    return 0;

                case LauncherCommandKind.ProfileAdd:
                {
                    var profile = ProfileManager.Add(config, command.ProfileMutation
                        ?? throw new InvalidOperationException("缺少 Profile 创建参数"));
                    return Save(store, config, output, error,
                        $"已创建 Profile：{profile.Id}（默认 Profile：{config.SelectedProfileId ?? "无"}）");
                }

                case LauncherCommandKind.ProfileSet:
                {
                    var profile = ProfileManager.Update(config, command.ProfileSelector!, command.ProfileMutation
                        ?? throw new InvalidOperationException("缺少 Profile 修改参数"));
                    return Save(store, config, output, error, $"已更新 Profile：{profile.Id}");
                }

                case LauncherCommandKind.ProfileSelect:
                {
                    var profile = ProfileManager.Select(config, command.ProfileSelector!);
                    return Save(store, config, output, error, $"已选择默认 Profile：{profile.Id}");
                }

                case LauncherCommandKind.ProfileRemove:
                {
                    var profile = ProfileManager.Remove(config, command.ProfileSelector!);
                    return Save(store, config, output, error,
                        $"已删除 Profile：{profile.Id}（当前默认 Profile：{config.SelectedProfileId ?? "无"}）");
                }

                default:
                    error.WriteLine("无效的 Profile 命令");
                    return 2;
            }
        }
        catch (LauncherException ex)
        {
            error.WriteLine(ex.Message);
            return 4;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Profile 操作失败：{ex.Message}");
            return 3;
        }
    }

    private static int Save(
        AppConfigStore store,
        AppConfig config,
        TextWriter output,
        TextWriter error,
        string successMessage)
    {
        try
        {
            store.Save(config);
            output.WriteLine(successMessage);
            output.WriteLine($"配置已原子保存：{store.ConfigPath}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"配置保存失败：{ex.Message}");
            return 3;
        }
    }

    private static async Task<int> MonitorSessionAsync(
        LauncherSession session,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        using var cancellation = new CancellationTokenSource();
        var quitRequested = false;
        Task? inputTask = null;
        if (!ReferenceEquals(input, Console.In) || !Console.IsInputRedirected)
        {
            inputTask = ReadInputAsync(session, input, output, error, cancellation, () => quitRequested = true);
        }

        var exitCode = 5;
        try
        {
            await foreach (var ev in session.Events.WithCancellation(cancellation.Token).ConfigureAwait(false))
            {
                output.WriteLine(FormatEvent(ev));
                if (ev.State is "Exited" or "Failed" or "HostCrashedBeforeRunning" or "HostCrashedAfterRunning")
                {
                    exitCode = ev.State == "Exited" ? 0 : 7;
                    cancellation.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (quitRequested)
        {
            exitCode = 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"会话事件流中断：{ex.Message}");
        }
        finally
        {
            cancellation.Cancel();
            if (inputTask is not null)
            {
                try
                {
                    await inputTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        return exitCode;
    }

    private static async Task ReadInputAsync(
        LauncherSession session,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationTokenSource cancellation,
        Action markQuit)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellation.Token).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (!LauncherInputParser.TryParse(line, out var command, out var parseError))
            {
                error.WriteLine(parseError);
                continue;
            }

            switch (command.Kind)
            {
                case LauncherInputCommandKind.Empty:
                    continue;
                case LauncherInputCommandKind.Quit:
                    markQuit();
                    cancellation.Cancel();
                    return;
                case LauncherInputCommandKind.SetTargetFps:
                    try
                    {
                        var response = await session.SetTargetFpsAsync(
                            command.TargetFps,
                            cancellation.Token).ConfigureAwait(false);
                        output.WriteLine(response.Applied
                            ? $"热调已应用：{command.TargetFps} FPS"
                            : $"热调失败：{response.Message}");
                    }
                    catch (Exception ex)
                    {
                        error.WriteLine($"热调失败：{ex.Message}");
                    }
                    break;
            }
        }
    }

    private static void WriteProfiles(AppConfig config, TextWriter output)
    {
        if (config.Profiles.Count == 0)
        {
            output.WriteLine("没有 Profile。请使用 profile add 创建。");
            return;
        }

        foreach (var profile in config.Profiles)
        {
            var marker = string.Equals(profile.Id, config.SelectedProfileId, StringComparison.Ordinal) ? "*" : " ";
            output.WriteLine($"{marker}\t{profile.Id}\t{profile.DisplayName}\t{FormatGame(profile.Game)}\t{profile.Variant}\t{profile.ExecutablePath}");
        }
    }

    private static void WriteProfile(GameProfile profile, TextWriter output)
    {
        output.WriteLine($"id: {profile.Id}");
        output.WriteLine($"name: {profile.DisplayName}");
        output.WriteLine($"game: {FormatGame(profile.Game)}");
        output.WriteLine($"variant: {profile.Variant}");
        output.WriteLine($"exe: {profile.ExecutablePath}");

        if (profile.Hoyo is not { } hoyo)
        {
            output.WriteLine("hoyo: <none>");
            return;
        }

        output.WriteLine($"fpsUnlockEnabled: {FormatOnOff(hoyo.FpsUnlockEnabled)}");
        output.WriteLine($"targetFps: {hoyo.TargetFps}");
        output.WriteLine($"backgroundFpsLimitEnabled: {FormatOnOff(hoyo.BackgroundFpsLimitEnabled)}");
        output.WriteLine($"backgroundFps: {hoyo.BackgroundFps}");
        output.WriteLine($"processPriority: {FormatPriority(hoyo.ProcessPriority)}");
        output.WriteLine($"genshinFollowInGamePreset: {FormatOnOff(hoyo.GenshinFollowInGamePreset)}");
        output.WriteLine($"genshinPreset30Fps: {hoyo.GenshinPreset30Fps}");
        output.WriteLine($"genshinPreset60Fps: {hoyo.GenshinPreset60Fps}");
        output.WriteLine($"genshinTouchUiScaleOverrideEnabled: {FormatOnOff(hoyo.GenshinTouchUiScaleOverrideEnabled)}");
        output.WriteLine($"genshinTouchUiScalePercent: {hoyo.GenshinTouchUiScalePercent}");
    }

    private static string FormatEvent(Sparxie.Contracts.Rpc.SessionEvent ev)
    {
        var suffix = string.IsNullOrWhiteSpace(ev.Message) ? string.Empty : $"：{ev.Message}";
        return $"[{ev.State}] stage={ev.Stage} error={ev.ErrorCode}{suffix}";
    }

    private static string FormatGame(GameType game) => game switch
    {
        GameType.Genshin => "genshin",
        GameType.StarRail => "starRail",
        GameType.ZenlessZoneZero => "zenlessZoneZero",
        _ => game.ToString(),
    };

    private static string FormatOnOff(bool value) => value ? "on" : "off";

    private static string FormatPriority(ProcessPriority priority) => priority switch
    {
        ProcessPriority.BelowNormal => "belowNormal",
        ProcessPriority.AboveNormal => "aboveNormal",
        ProcessPriority.High => "high",
        _ => "normal",
    };

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Sparxie.Launcher");
        writer.WriteLine("  list");
        writer.WriteLine("  profile list");
        writer.WriteLine("  profile show <profile-id-or-name>");
        writer.WriteLine("  profile add --id <id> --name <名称> --game <genshin|starRail|zenlessZoneZero> --variant <variant> --exe <完整EXE路径> [设置选项]");
        writer.WriteLine("  profile set <profile-id-or-name> [--name <名称>] [--variant <variant>] [--exe <完整EXE路径>] [设置选项]");
        writer.WriteLine("  profile select <profile-id-or-name>");
        writer.WriteLine("  profile remove <profile-id-or-name>");
        writer.WriteLine("  launch [profile-id-or-name]");
        writer.WriteLine("  Hoyo 设置：--fps <on|off> --target-fps <10-1000> --background-fps-limit <on|off> --background-fps <10-1000> --priority <normal|belowNormal|aboveNormal|high>");
        writer.WriteLine("  原神专属：--follow-in-game-preset <on|off> --preset-30-fps <10-1000> --preset-60-fps <10-1000> --touch-ui-scale-override <on|off> --touch-ui-scale <100-500>");
        writer.WriteLine("  launch 运行中输入：fps <10-1000>，或 quit 关闭控制端");
    }
}
