using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;

namespace Sparxie.Desktop.ViewModels;

/// <summary>Flyout 的配色：当前激活项用主题色（随背景图提取，与启动按钮同源），其余一律灰。</summary>
internal static class SwitcherBrushes
{
    public static readonly Brush Transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Gray = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    public static readonly Brush White = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    public static Brush Accent => (Brush)Application.Current.Resources["LaunchAccentBrush"];

    public static Brush OnAccent => (Brush)Application.Current.Resources["LaunchAccentForegroundBrush"];
}

/// <summary>主页游戏切换 Flyout 中的一行：一个游戏及其配置 Chip（仅多配置时展示）。</summary>
public sealed class GameEntryViewModel
{
    public GameEntryViewModel(GameType game, bool isSelected, IReadOnlyList<ProfileChipViewModel> profiles, Action select)
    {
        Game = game;
        IsSelected = isSelected;
        Profiles = profiles;
        SelectCommand = new RelayCommand(select);
    }

    public GameType Game { get; }

    public string Title => GameNames.Display(Game);

    public ImageSource? Icon => GameTabBinding.Icon(Game);

    public bool IsSelected { get; }

    /// <summary>单配置的游戏没有 Chip，标题行本身就是“配置”：激活时整行用主题色。</summary>
    private bool IsActiveSingle => IsSelected && !HasMultipleProfiles;

    public Brush RowBackground => IsActiveSingle ? SwitcherBrushes.Accent : SwitcherBrushes.Transparent;

    /// <summary>激活的单配置行用主题色底配对比色字；多配置游戏的标题保持白色，激活态由其下的 Chip 表示。</summary>
    public Brush TitleForeground => IsActiveSingle ? SwitcherBrushes.OnAccent : SwitcherBrushes.White;

    public IReadOnlyList<ProfileChipViewModel> Profiles { get; }

    /// <summary>该游戏有多套配置时才展示 Chip；单配置时不出现配置概念。</summary>
    public bool HasMultipleProfiles => Profiles.Count > 1;

    public string AutomationName => IsSelected ? $"{Title}，当前" : $"切换到 {Title}";

    public RelayCommand SelectCommand { get; }
}

public sealed class ProfileChipViewModel
{
    private readonly bool _isActive;
    private readonly bool _isRemembered;

    /// <param name="isActive">当前游戏的当前配置：主题色。</param>
    /// <param name="isRemembered">该游戏点标题时会落到的配置：灰底，其余只有描边。</param>
    public ProfileChipViewModel(GameProfile profile, bool isActive, bool isRemembered, Action select)
    {
        DisplayName = profile.DisplayName;
        _isActive = isActive;
        _isRemembered = isRemembered;
        SelectCommand = new RelayCommand(select);
    }

    public string DisplayName { get; }

    public Brush ChipBackground => _isActive
        ? SwitcherBrushes.Accent
        : _isRemembered ? SwitcherBrushes.Gray : SwitcherBrushes.Transparent;

    public Brush ChipForeground => _isActive ? SwitcherBrushes.OnAccent : SwitcherBrushes.White;

    public string AutomationName => _isActive ? $"{DisplayName}，当前配置" : $"切换到 {DisplayName}";

    public RelayCommand SelectCommand { get; }
}
