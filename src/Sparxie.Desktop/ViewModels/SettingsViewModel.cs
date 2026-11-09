using Sparxie.Desktop.Services;

namespace Sparxie.Desktop.ViewModels;

/// <summary>应用设置：所有项改动即保存（没有保存按钮）。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly DesktopDataService _data;
    private int _closeBehaviorIndex;
    private bool _debugMode;
    private bool _loading;

    public SettingsViewModel(DesktopDataService data)
    {
        _data = data;
        _closeBehaviorIndex = (int)data.Preferences.ResolveCloseBehavior();
        _debugMode = data.Preferences.DebugMode;
    }

    /// <summary>设置保存成功后触发，让主页、托盘等依赖偏好的部分刷新。</summary>
    public event Action? Saved;

    /// <summary>0 = 询问，1 = 最小化到托盘，2 = 直接退出（与 <see cref="CloseBehavior"/> 一一对应）。</summary>
    public int CloseBehaviorIndex
    {
        get => _closeBehaviorIndex;
        set
        {
            if (value < 0 || value > 2)
            {
                return; // ComboBox 在重建时会回写 -1，忽略
            }

            if (SetProperty(ref _closeBehaviorIndex, value))
            {
                SaveNow();
            }
        }
    }

    public CloseBehavior CurrentCloseBehavior => (CloseBehavior)_closeBehaviorIndex;

    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetProperty(ref _debugMode, value))
            {
                SaveNow();
            }
        }
    }

    private void SaveNow()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _data.Preferences.CloseAction = ((CloseBehavior)_closeBehaviorIndex).ToString();
            
            _data.Preferences.DebugMode = _debugMode;
            _data.SavePreferences();
            Saved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Write("保存桌面偏好失败", ex);
        }
    }
}
