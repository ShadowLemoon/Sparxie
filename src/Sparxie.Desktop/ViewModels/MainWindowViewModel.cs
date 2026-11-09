using Sparxie.Desktop.Services;

namespace Sparxie.Desktop.ViewModels;

public enum DesktopPage
{
    Home,
    Profiles,
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly SettingsPanelState _settingsPanel;
    private DesktopPage _currentPage = DesktopPage.Home;

    public MainWindowViewModel(
        HomeViewModel home,
        ProfileManagerViewModel profiles,
        SettingsViewModel settings,
        SettingsPanelState settingsPanel)
    {
        Home = home;
        Profiles = profiles;
        Settings = settings;
        _settingsPanel = settingsPanel;
        _settingsPanel.Changed += OnSettingsPanelChanged;
        ShowHomeCommand = new RelayCommand(ShowHome);
        ShowProfilesCommand = new RelayCommand(ShowProfiles);
        ToggleSettingsCommand = new RelayCommand(_settingsPanel.Toggle);
        CloseSettingsCommand = new RelayCommand(_settingsPanel.Close);
    }

    public HomeViewModel Home { get; }

    public ProfileManagerViewModel Profiles { get; }

    public SettingsViewModel Settings { get; }

    public RelayCommand ShowHomeCommand { get; }

    public RelayCommand ShowProfilesCommand { get; }

    public RelayCommand ToggleSettingsCommand { get; }

    public RelayCommand CloseSettingsCommand { get; }

    public DesktopPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsHomeVisible));
            OnPropertyChanged(nameof(IsProfilesVisible));
            OnPropertyChanged(nameof(IsHomeChromeVisible));
        }
    }

    public bool IsHomeVisible => CurrentPage == DesktopPage.Home;

    public bool IsProfilesVisible => CurrentPage == DesktopPage.Profiles;

    /// <summary>主页专属控件（游戏标签、Profile/启动按钮）的可见性：仅主页且设置抽屉关闭时显示。</summary>
    public bool IsHomeChromeVisible => CurrentPage == DesktopPage.Home && !_settingsPanel.IsOpen;

    public bool IsSettingsOpen => _settingsPanel.IsOpen;

    private void ShowHome()
    {
        Profiles.Refresh();
        Home.Refresh();
        CurrentPage = DesktopPage.Home;
    }

    private void ShowProfiles()
    {
        _settingsPanel.Close();
        Profiles.EnsureDefaultIfEmpty(Home.SelectedGame);
        Profiles.Refresh();
        CurrentPage = DesktopPage.Profiles;
    }

    private void OnSettingsPanelChanged()
    {
        OnPropertyChanged(nameof(IsSettingsOpen));
        OnPropertyChanged(nameof(IsHomeChromeVisible));
    }
}
