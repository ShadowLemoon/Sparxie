using System.Text.Json;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Endfield;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// Keeps endfield-ui.ini equal to the touch-interface settings of the session's profile: once before the game starts, then whenever config.json is saved
/// while the game runs (the Runtime re-reads the ini every few seconds, so a change in the settings page reaches the running game without a restart).
/// Nothing here may fail a launch or end a session: errors go to the launch log and the next change is tried again.
/// </summary>
internal sealed class EndfieldUiSync : IDisposable
{
    private readonly string _profileId;
    private readonly string _iniPath;
    private readonly string _configPath;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private (DateTime WriteTimeUtc, long Length)? _seen;
    private Task? _loop;

    public EndfieldUiSync(string profileId, string iniPath, string configPath, Action<string> log)
    {
        _profileId = profileId;
        _iniPath = iniPath;
        _configPath = configPath;
        _log = log;
    }

    /// <summary>The two ini lines for these settings.</summary>
    public static KeyValuePair<string, string>[] Lines(HoyoProfileSettings? settings)
    {
        var (scale, safeArea) = EndfieldUiSettings.IniValues(settings);
        return
        [
            new(EndfieldUiSettings.ScaleKey, scale),
            new(EndfieldUiSettings.SafeAreaKey, safeArea),
        ];
    }

    /// <summary>Writes the ini for <paramref name="settings"/>; true when the file changed. Failures are logged and reported as false.</summary>
    public bool Write(HoyoProfileSettings? settings)
    {
        var lines = Lines(settings);
        try
        {
            var changed = EndfieldUiIni.Apply(_iniPath, lines);
            if (changed)
            {
                _log($"endfield-ui.ini: {string.Join(' ', lines.Select(l => $"{l.Key}={l.Value}"))}");
            }

            return changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log($"endfield-ui.ini not written: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts following config.json. The state of the file at this moment counts as already applied.</summary>
    public void Start(TimeSpan interval)
    {
        _seen = Stamp();
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    Poll();
                }
            }
            catch (OperationCanceledException)
            {
                // the session ended
            }
        });
    }

    /// <summary>One look at config.json; true when it had changed and the ini was rewritten. Public to the tests.</summary>
    internal bool Poll()
    {
        var stamp = Stamp();
        if (stamp is null || stamp == _seen)
        {
            return false;
        }

        HoyoProfileSettings? settings;
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            AppConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(bytes, ConfigJsonOptions.Read);
            }
            catch (JsonException)
            {
                config = null;  // read only: a file that does not parse is treated like a missing profile
            }

            var profile = config?.Profiles.FirstOrDefault(p => string.Equals(p.Id, _profileId, StringComparison.Ordinal));
            if (profile is null || profile.Game != GameType.Endfield)
            {
                // a profile that is gone, a file that does not parse or a half-saved one leaves the game as it is; the file is looked at again when it changes
                _seen = stamp;
                return false;
            }

            settings = profile.Hoyo;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // saved at this very moment: the stamp stays unseen, so the next poll tries again
            return false;
        }

        _seen = stamp;
        return Write(settings);
    }

    private (DateTime, long)? Stamp()
    {
        try
        {
            var info = new FileInfo(_configPath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the loop only ends by cancellation
        }

        _stop.Dispose();
    }
}
