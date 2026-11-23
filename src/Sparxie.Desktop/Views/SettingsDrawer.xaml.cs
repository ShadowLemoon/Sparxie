using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Sparxie.Desktop.ViewModels;

namespace Sparxie.Desktop.Views;

public sealed partial class SettingsDrawer : UserControl
{
    private bool _dialogOpen;

    public SettingsDrawer()
    {
        InitializeComponent();

        // “关于”里的大图标；文件缺失时不显示（不影响其余设置项）。
        var png = Path.Combine(AppContext.BaseDirectory, "sparxie-256.png");
        if (File.Exists(png))
        {
            AboutIcon.Source = new BitmapImage(new Uri(png));
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Settings.Plugins.Confirm = ConfirmPluginAsync;
            }
        };
    }

    private async void ImportPluginZipClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".zip");
        await ImportPluginAsync(picker, async () => (await picker.PickSingleFileAsync())?.Path);
    }

    private async void ImportPluginFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        await ImportPluginAsync(picker, async () => (await picker.PickSingleFolderAsync())?.Path);
    }

    private async Task ImportPluginAsync(object picker, Func<Task<string?>> pick)
    {
        if (DataContext is not MainWindowViewModel viewModel || App.MainWindow is not { } window)
        {
            return;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await pick() is { Length: > 0 } path)
        {
            await viewModel.Settings.Plugins.ImportAsync(path);
        }
    }

    /// <summary>插件启用/删除前的确认对话框：默认按钮是“取消”，正文是完整的来源、哈希和风险说明。</summary>
    private async Task<bool> ConfirmPluginAsync(PluginConfirmation confirmation)
    {
        if (_dialogOpen || XamlRoot is null)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = confirmation.Title,
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock { Text = confirmation.Body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
                PrimaryButtonText = confirmation.PrimaryText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
