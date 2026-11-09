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
        _launcher ??= new LauncherClient(new BrokerClient(options: CreateOptions()));
        return await _launcher.StartSessionAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    private static BrokerClientOptions CreateOptions()
    {
        // 排障用：设置 SPARXIE_TEST_NO_ELEVATE=1 时以当前权限直接启动 Broker（不弹 UAC），仅用于自动化验证连接链路。
        var noElevate = Environment.GetEnvironmentVariable("SPARXIE_TEST_NO_ELEVATE") == "1";
        return new BrokerClientOptions
        {
            Diagnostics = (message, exception) =>
            {
                if (exception is null)
                {
                    DesktopLog.Write(message);
                }
                else
                {
                    DesktopLog.Write(message, exception);
                }
            },
            ProcessStarter = startInfo =>
            {
                DesktopLog.Write($"启动 Broker：{startInfo.FileName} {startInfo.Arguments}（提权={!noElevate}）");
                if (noElevate)
                {
                    startInfo.Verb = string.Empty;
                    startInfo.UseShellExecute = false;
                }

                return System.Diagnostics.Process.Start(startInfo);
            },
        };
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
