using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Sparxie.Desktop.Views;

public sealed partial class SettingsDrawer : UserControl
{
    public SettingsDrawer()
    {
        InitializeComponent();

        // “关于”里的大图标；文件缺失时不显示（不影响其余设置项）。
        var png = Path.Combine(AppContext.BaseDirectory, "sparxie-256.png");
        if (File.Exists(png))
        {
            AboutIcon.Source = new BitmapImage(new Uri(png));
        }
    }
}
