using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sparxie.Desktop.ViewModels;

public sealed class HomeViewModel : INotifyPropertyChanged
{
    private string _currentProfile = "未选择配置";
    private bool _isSettingsOpen;

    public string CurrentProfile
    {
        get => _currentProfile;
        set
        {
            if (_currentProfile == value) return;
            _currentProfile = value;
            OnPropertyChanged();
        }
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (_isSettingsOpen == value) return;
            _isSettingsOpen = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
