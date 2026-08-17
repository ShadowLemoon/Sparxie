using System.Diagnostics;
using Sparxie.LauncherCore;

namespace LauncherCore.Tests;

public sealed class BrokerClientOptionsTests
{
    [Fact]
    public async Task BrokerClient使用runas且不通过Environment传管道()
    {
        ProcessStartInfo? captured = null;
        var options = new BrokerClientOptions
        {
            BrokerExecutablePath = Environment.ProcessPath,
            ConnectTimeout = TimeSpan.FromMilliseconds(50),
            RetryDelay = TimeSpan.Zero,
            ProcessStarter = startInfo =>
            {
                captured = startInfo;
                return null;
            },
        };
        await using var client = new BrokerClient("sparxie-test-123", options);

        await Assert.ThrowsAsync<LauncherException>(() => client.EnsureConnectedAsync());

        Assert.NotNull(captured);
        Assert.True(captured!.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.Contains("--pipe-name=sparxie-test-123", captured.Arguments);
        Assert.DoesNotContain("SPARXIE_PIPE_NAME", captured.Environment.Keys);
    }
}
