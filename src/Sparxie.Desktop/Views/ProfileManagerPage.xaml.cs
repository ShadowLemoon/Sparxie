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
