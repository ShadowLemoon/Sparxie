using Sparxie.Contracts.Rpc;
using Xunit;

namespace Broker.Tests;

public class CapabilityScreenArgsTests
{
    [Fact]
    public void 契约列出ScreenArgs能力()
    {
        Assert.Contains(RpcContract.CapabilityScreenArgs, RpcContract.Capabilities);
        Assert.Equal("screen-args", RpcContract.CapabilityScreenArgs);
    }
}
