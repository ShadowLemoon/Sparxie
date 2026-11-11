using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Sparxie.Desktop.Views;

public sealed partial class LaunchSettingsCard : UserControl
{
    public LaunchSettingsCard() => InitializeComponent();

    /// <summary>按回车 = 编辑完成：把焦点移走，文本框随之把文字写回（WinUI 的文本框没有 editingFinished，失焦就是它）。</summary>
    private void CommitOnEnter(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
            e.Handled = true;
        }
    }
}
