using Sparxie.Contracts.Models;
using Sparxie.LauncherCore;

namespace Sparxie.Desktop.Services;

/// <summary>Thin desktop adapter over LauncherCore. No process or Broker logic is duplicated here.</summary>
public sealed class LauncherService : IAsyncDisposable
{
    private LauncherClient? _launcher;

    public async Task<LauncherSession> StartAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _launcher ??= new LauncherClient(new BrokerClient());
        return await _launcher.StartSessionAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_launcher is not null)
        {
            await _launcher.DisposeAsync().ConfigureAwait(false);
            _launcher = null;
        }
    }
}
