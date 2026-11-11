using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sparxie.Desktop.Services;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Desktop.Views;

public sealed partial class ProfileManagerPage : Page
{
    public ProfileManagerPage() => InitializeComponent();

    private ProfileManagerViewModel? Profiles => (DataContext as MainWindowViewModel)?.Profiles;

    /// <summary>两栏所需的最小面板宽度：左栏 380 + 间距 16 + 右栏（帧率档位映射一行最宽）约 540 + 左右内边距 40。</summary>
    private const double TwoColumnMinWidth = 980;

    /// <summary>原神 / 星铁设置页的响应式布局：够宽时 380 | * 两栏，窄了右栏（帧率、触控 UI 缩放）折到左栏下面。</summary>
    private void HoyoGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= TwoColumnMinWidth;
        HoyoLeftColumnDefinition.Width = wide ? new GridLength(380) : new GridLength(1, GridUnitType.Star);
        HoyoRightColumnDefinition.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(HoyoRightColumn, wide ? 1 : 0);
        Grid.SetRow(HoyoRightColumn, wide ? 0 : 1);
    }

    private void AddProfileClick(object sender, RoutedEventArgs e) => Profiles?.NewCommand.Execute(null);

    private void CopyProfileClick(object sender, RoutedEventArgs e) => Profiles?.CopyCommand.Execute(null);

    /// <summary>MenuFlyout 不在可视树内、绑定取不到页面上下文，打开时按当前配置数决定是否显示“删除”。</summary>
    private void MoreMenuOpening(object? sender, object e) =>
        DeleteMenuItem.Visibility = Profiles?.HasMultiple == true ? Visibility.Visible : Visibility.Collapsed;

    private async void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (Profiles is not { SelectedProfile: { } selected } profiles)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除此配置？",
            Content = $"将删除“{selected.DisplayName}”，此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        profiles.DeleteCommand.Execute(null);
    }

    private async void BrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel viewModel
                || viewModel.Profiles.SelectedProfile is not { } profile
                || App.MainWindow is not { } window)
            {
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            picker.FileTypeFilter.Add(".exe");

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            // 选完立即按当前游戏白名单反馈，避免用户填完再在保存时才看到失败。
            if (!GameExecutables.IsAllowed(profile.Game, file.Path))
            {
                var allowed = string.Join("、", GameExecutables.For(profile.Game));
                await ShowMessageAsync($"该 EXE 不属于所选游戏，可用文件名：{allowed}");
                return;
            }

            profile.ExecutablePath = file.Path;
        }
        catch (Exception ex)
        {
            DesktopLog.Write("选择游戏 EXE 失败", ex);
            await ShowMessageAsync($"选择文件失败：{ex.Message}");
        }
    }

    private async Task ShowMessageAsync(string message)
    {
        try
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "游戏 EXE",
                Content = message,
                CloseButtonText = "知道了",
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            DesktopLog.Write("显示 EXE 提示对话框失败", ex);
        }
    }
}
