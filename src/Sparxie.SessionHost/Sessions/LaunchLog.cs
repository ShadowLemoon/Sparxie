using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Configuration;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// One line per session into logs\game-launch.log: the title and the screen arguments the session puts on the game's command line, so a launch can be
/// checked afterwards without starting the game again. A diagnostics line must never fail a launch.
/// </summary>
internal static class LaunchLog
{
    public static string Line(ProfileSnapshot profile, DateTimeOffset now)
    {
        var arguments = ScreenArguments.Build(profile.Screen);
        return $"[{now:O}] game={profile.Game} profile={profile.ProfileId} exe={Path.GetFileName(profile.ExecutablePath)} screen-args={(arguments.Length == 0 ? "(none)" : arguments)}";
    }

    public static void Write(ProfileSnapshot profile)
    {
        try
        {
            var directory = Path.Combine(AppDataDirectory.Resolve(), "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "game-launch.log"), Line(profile, DateTimeOffset.Now) + Environment.NewLine);
        }
        catch (Exception)
        {
            // see the summary
        }
    }
}
