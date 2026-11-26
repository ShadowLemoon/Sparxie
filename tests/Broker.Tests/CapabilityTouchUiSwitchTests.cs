using Sparxie.Contracts.Rpc;
using Xunit;

namespace Broker.Tests;

public class CapabilityTouchUiSwitchTests
{
    [Fact]
    public void 契约列出TouchUiSwitch能力()
    {
        Assert.Contains(RpcContract.CapabilityTouchUiSwitch, RpcContract.Capabilities);
        Assert.Equal("touch-ui-switch", RpcContract.CapabilityTouchUiSwitch);
    }
}
