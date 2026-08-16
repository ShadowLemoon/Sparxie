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

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error)
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

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var loadResult = new AppConfigStore(configPath).Load();
        if (loadResult.State == ConfigLoadState.Failed)
        {
            error.WriteLine(loadResult.Error?.Message ?? "配置加载失败");
            return 3;
        }

        if (loadResult.State == ConfigLoadState.RestoredFromCorrupt)
        {
            output.WriteLine($"警告：旧配置异常，已备份为 {Path.GetFileName(loadResult.BackupPath)}，当前使用空白配置。");
        }

        if (parsed.Command.Kind == LauncherCommandKind.List)
        {
            WriteProfiles(loadResult.Config, output);
            return 0;
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

        output.WriteLine($"准备启动：{profile.DisplayName} ({profile.Game})");
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

    private static void WriteProfiles(Sparxie.Contracts.Models.AppConfig config, TextWriter output)
    {
        if (config.Profiles.Count == 0)
        {
            output.WriteLine("没有 Profile");
            return;
        }

        foreach (var profile in config.Profiles)
        {
            output.WriteLine($"{profile.Id}\t{profile.DisplayName}\t{profile.Game}\t{profile.Variant}\t{profile.ExecutablePath}");
        }
    }

    private static string FormatEvent(Sparxie.Contracts.Rpc.SessionEvent ev)
    {
        var suffix = string.IsNullOrWhiteSpace(ev.Message) ? string.Empty : $"：{ev.Message}";
        return $"[{ev.State}] stage={ev.Stage} error={ev.ErrorCode}{suffix}";
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Sparxie.Launcher");
        writer.WriteLine("  list");
        writer.WriteLine("  launch [profile-id-or-name]");
        writer.WriteLine("  launch 运行中输入：fps <10-1000>，或 quit 关闭控制端");
    }
}
