using Sparxie.Broker.Sessions;

namespace Broker.Tests;

public sealed class SessionRegistryTests
{
    [Fact]
    public void 控制流只允许一个持有者()
    {
        var registry = new SessionRegistry();

        Assert.True(registry.TryAcquireControlStream());
        Assert.False(registry.TryAcquireControlStream());
        Assert.True(registry.HasControlStream);

        registry.ReleaseControlStream();

        Assert.False(registry.HasControlStream);
        Assert.True(registry.TryAcquireControlStream());
    }
}
