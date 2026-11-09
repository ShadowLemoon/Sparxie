using Sparxie.Desktop.Services;

namespace Sparxie.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly DesktopDataService _data;
    private bool _confirmBeforeExit;
    private string _statusMessage = string.Empty;

    public SettingsViewModel(DesktopDataService data)
    {
        _data = data;
        _confirmBeforeExit = data.Preferences.ConfirmBeforeExit;
        SaveCommand = new RelayCommand(Save);
    }

    public RelayCommand SaveCommand { get; }

    public bool ConfirmBeforeExit
    {
        get => _confirmBeforeExit;
        set => SetProperty(ref _confirmBeforeExit, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private void Save()
    {
        try
        {
            _data.Preferences.ConfirmBeforeExit = ConfirmBeforeExit;
            _data.SavePreferences();
            StatusMessage = "应用设置已保存";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            DesktopLog.Write("保存桌面偏好失败", ex);
        }
    }
}
