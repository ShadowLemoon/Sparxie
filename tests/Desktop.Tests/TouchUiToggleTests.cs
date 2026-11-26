using Sparxie.Contracts.Models;
using Sparxie.Desktop.ViewModels;

namespace Desktop.Tests;

/// <summary>原神 / 星铁配置页的触屏界面开关：与帧率解锁至少开一个。</summary>
public sealed class TouchUiToggleTests
{
    private static ProfileEditorViewModel Editor(GameType game, bool touchUi = true, bool fpsUnlock = true) =>
        ProfileEditorViewModel.From(new GameProfile
        {
            Id = "p1",
            DisplayName = "配置",
            Game = GameIds.ToId(game),
            ExecutablePath = string.Empty,
            Hoyo = game == GameType.ZenlessZoneZero ? null : new HoyoProfileSettings { TouchUiEnabled = touchUi, FpsUnlockEnabled = fpsUnlock },
        });

    [Theory]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(false, false, true, true, true)] // 手改配置出的无效组合：两个都能打开
    public void 只剩一个开着时锁住它(bool touchUi, bool fpsUnlock, bool canTouch, bool canFps, bool showRule)
    {
        var editor = Editor(GameType.StarRail, touchUi, fpsUnlock);

        Assert.Equal(canTouch, editor.CanToggleTouchUi);
        Assert.Equal(canFps, editor.CanToggleFpsUnlock);
        Assert.Equal(showRule, editor.ShowTouchUiRule);
    }

    [Fact]
    public void 开关值读出并写回()
    {
        var editor = Editor(GameType.StarRail, touchUi: false);

        Assert.False(editor.TouchUiEnabled);
        Assert.False(editor.ToMutation().TouchUiEnabled);
        editor.TouchUiEnabled = true;
        Assert.True(editor.ToMutation().TouchUiEnabled);
    }

    [Fact]
    public void 绝区零不写触屏开关()
    {
        Assert.Null(Editor(GameType.ZenlessZoneZero).ToMutation().TouchUiEnabled);
    }

    [Fact]
    public void 关闭触屏界面后隐藏原神触控缩放()
    {
        var editor = Editor(GameType.Genshin);
        Assert.True(editor.ShowGenshinTouchUiScale);

        editor.TouchUiEnabled = false;

        Assert.False(editor.ShowGenshinTouchUiScale);
        Assert.False(Editor(GameType.StarRail).ShowGenshinTouchUiScale);
    }
}
