using Sparxie.Infrastructure.Processes;
using Xunit;

namespace Broker.Tests;

public class GameExitDescriptionTests
{
    [Fact]
    public void 有退出码时同时给出退出码与运行时长()
    {
        var text = GameExitDescription.Describe(0, TimeSpan.FromSeconds(3725));
        Assert.Equal("游戏已退出，退出码 0，运行 01:02:05", text);
    }

    [Fact]
    public void 无退出码时只给出运行时长且超过一天不回绕()
    {
        var text = GameExitDescription.Describe(null, TimeSpan.FromHours(25) + TimeSpan.FromMinutes(1));
        Assert.Equal("游戏已退出，运行 25:01:00", text);
    }
}
