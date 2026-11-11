using Sparxie.Desktop.Services;

namespace Desktop.Tests;

public sealed class GameBackgroundPickTests
{
    private static readonly GameBackgroundItem[] Items =
    [
        new("a", @"C:\cache\a.img", null),
        new("b", @"C:\cache\b.img", "https://example.com/b.mp4"),
        new("c", @"C:\cache\c.img", null),
    ];

    [Fact]
    public void 记住的那张存在就选它()
    {
        Assert.Equal("b", GameBackgroundService.Pick(Items, "b")!.Id);
    }

    [Fact]
    public void 没有记录或记录的已被官方撤掉时选第一张()
    {
        Assert.Equal("a", GameBackgroundService.Pick(Items, null)!.Id);
        Assert.Equal("a", GameBackgroundService.Pick(Items, "gone")!.Id);
    }

    [Fact]
    public void 空列表返回null()
    {
        Assert.Null(GameBackgroundService.Pick([], "a"));
    }

    [Fact]
    public void 只有带视频地址的才算动态背景()
    {
        Assert.False(Items[0].HasVideo);
        Assert.True(Items[1].HasVideo);
    }
}
