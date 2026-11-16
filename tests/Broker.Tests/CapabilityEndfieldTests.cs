using Sparxie.Contracts.Rpc;
using Xunit;

namespace Broker.Tests;

public class CapabilityEndfieldTests
{
    [Fact]
    public void 契约列出Endfield能力()
    {
        Assert.Contains(RpcContract.CapabilityEndfield, RpcContract.Capabilities);
        Assert.Equal("endfield", RpcContract.CapabilityEndfield);
    }
}
