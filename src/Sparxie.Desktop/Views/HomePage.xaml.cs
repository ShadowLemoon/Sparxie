using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sparxie.Desktop.ViewModels;

namespace Sparxie.Desktop.Views;

public sealed partial class HomePage : Page
{
    public HomePage() => InitializeComponent();

    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ProfileFlyout.Hide();
        if (sender is ListView { SelectedItem: ProfileListItemViewModel item }
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Home.SelectProfileCommand.Execute(item);
        }
    }

    private void ProfileItemClicked(object sender, ItemClickEventArgs e)
    {
        ProfileFlyout.Hide();
        if (e.ClickedItem is ProfileListItemViewModel item
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Home.SelectProfileCommand.Execute(item);
        }
    }

    private void ManageProfilesClicked(object sender, RoutedEventArgs e) => ProfileFlyout.Hide();
}
