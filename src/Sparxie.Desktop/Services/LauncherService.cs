namespace Sparxie.Desktop.Services;

public sealed class LauncherService
{
    public Task StartAsync(string profileId, CancellationToken cancellationToken = default)
    {
        // TODO: connect to Sparxie.LauncherCore SessionHost.
        // The desktop layer must not duplicate CLI launch logic.
        return Task.CompletedTask;
    }
}
