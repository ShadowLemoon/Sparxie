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

    /// <summary>
    /// 启用确认框正文：先风险，再能力，再来源，最后是等宽分组的哈希（可复制）；文件变化时并排显示上次确认的和现在的哈希。
    /// 必选勾选框收集在 <paramref name="acks"/> 里，由调用方控制“启用”按钮。
    /// </summary>
    private static StackPanel BuildEnableContent(PluginEnableDetails d, List<CheckBox> acks)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };

        if (d.IsChange)
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Title = "这个插件的文件和你上次确认的不一样",
                Message = "可能是你更新了插件，也可能是文件被替换了。请对照下面的新旧哈希，确认来源可信再启用。",
            });
        }

        var risks = new StackPanel { Spacing = 4 };
        foreach (var sentence in d.RiskSentences)
        {
            risks.Children.Add(new TextBlock { Text = "• " + sentence, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(risks);

        if (d.Capabilities.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "它声明的能力：" + string.Join("、", d.Capabilities), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        }

        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return; }
            var line = new StackPanel { Spacing = 2 };
            line.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 });
            line.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            panel.Children.Add(line);
        }

        Row("名称 / 标识", $"{d.Name}（{d.Id}）");
        Row("版本 / 许可", string.Join(" · ", new[] { d.Version, d.License }.Where(text => !string.IsNullOrWhiteSpace(text))));
        Row("主页" + "（作者自述，未经 Sparxie 验证）", d.Homepage);
        const string ClaimTag = "（作者自述，未经 Sparxie 验证）";
        Row("作者" + ClaimTag, d.Author);
        Row("简介" + ClaimTag, d.Description);
        Row("数据与联网说明" + ClaimTag, d.PrivacyNote);
        if (d.Links is { Count: > 0 })
        {
            Row("相关链接" + ClaimTag, string.Join("\n", d.Links.Select(link => $"{link.Key}：{link.Value}")));
        }

        if (d.IsChange)
        {
            Row("版本变化", d.PreviousVersion is null ? null : $"{d.PreviousVersion} → {d.Version ?? "（未声明）"}");
            if (d.AddedCapabilities is { Count: > 0 })
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "新增的能力：" + string.Join("、", d.AddedCapabilities),
                    TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
            }
        }

        Row("可启动的程序", string.Join("、", d.ExeNames));
        Row("位置", d.Directory);

        void Hash(string label, string hash, bool dim = false)
        {
            var line = new StackPanel { Spacing = 2, Opacity = dim ? 0.7 : 1 };
            line.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 });
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = PluginsViewModel.FormatHash(hash), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            });
            var copy = new Button { Content = "复制", MinHeight = 32, Padding = new Thickness(10, 0, 10, 0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, $"复制{label}");
            copy.Click += (_, _) =>
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(hash);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            };
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);
            line.Children.Add(row);
            panel.Children.Add(line);
        }

        if (d.IsChange)
        {
            Hash("上次确认的 module.dll SHA-256", d.PreviousDllSha256 ?? string.Empty, dim: true);
        }

        Hash(d.IsChange ? "现在的 module.dll SHA-256" : "module.dll SHA-256", d.DllSha256);
        if (d.IsChange)
        {
            Hash("上次确认的 plugin.json SHA-256", d.PreviousManifestSha256 ?? string.Empty, dim: true);
        }

        Hash(d.IsChange ? "现在的 plugin.json SHA-256" : "plugin.json SHA-256", d.ManifestSha256);
        Row("module.dll 大小", $"{d.DllSize:N0} 字节");

        var general = new CheckBox { Content = "我已核对上面的来源和哈希，并了解它会以管理员权限运行未经审计的原生代码", Margin = new Thickness(0, 4, 0, 0) };
        acks.Add(general);
        panel.Children.Add(general);
        if (d.RequiresInjectionAck)
        {
            var inject = new CheckBox { Content = "我了解向游戏注入代码可能违反游戏的用户协议，并有被封号的风险" };
            acks.Add(inject);
            panel.Children.Add(inject);
        }

        return panel;
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
            var acks = new List<CheckBox>();
            UIElement body = confirmation.Enable is { } details
                ? BuildEnableContent(details, acks)
                : new TextBlock { Text = confirmation.Body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = confirmation.Title,
                Content = new ScrollViewer { MaxHeight = 480, Content = body },
                PrimaryButtonText = confirmation.PrimaryText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = acks.Count == 0, // 有勾选框时，全部勾选后才能启用
            };
            void UpdatePrimary() => dialog.IsPrimaryButtonEnabled = acks.All(box => box.IsChecked == true);
            foreach (var box in acks)
            {
                box.Checked += (_, _) => UpdatePrimary();
                box.Unchecked += (_, _) => UpdatePrimary();
            }

            if (acks.Count > 0)
            {
                dialog.Opened += (_, _) => acks[0].Focus(FocusState.Programmatic); // 焦点放在勾选框，而不是“启用”
            }

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
