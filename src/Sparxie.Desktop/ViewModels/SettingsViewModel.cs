using System.Diagnostics;
using System.Reflection;
using Sparxie.Desktop.Services;
using Sparxie.Infrastructure.Diagnostics;

namespace Sparxie.Desktop.ViewModels;

/// <summary>应用设置：所有项改动即保存（没有保存按钮）。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly DesktopDataService _data;
    private int _closeBehaviorIndex;
    private int _afterLaunchIndex;
    private bool _debugMode;
    private bool _loading;
    private string _toolMessage = string.Empty;

    public SettingsViewModel(DesktopDataService data)
    {
        _data = data;
        _closeBehaviorIndex = (int)data.Preferences.ResolveCloseBehavior();
        _afterLaunchIndex = data.Preferences.ResolveAfterLaunch() == AfterLaunchBehavior.Minimize ? 1 : 0;
        _debugMode = data.Preferences.DebugMode;
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataRoot));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.LogsDirectory));
        OpenInstallFolderCommand = new RelayCommand(() => OpenFolder(AppContext.BaseDirectory));
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
    }

    /// <summary>设置保存成功后触发，让主页、托盘等依赖偏好的部分刷新。</summary>
    public event Action? Saved;

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public RelayCommand OpenInstallFolderCommand { get; }

    public RelayCommand ExportDiagnosticsCommand { get; }

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

    /// <summary>0 = 保持不变，1 = 游戏运行后最小化主窗口（与 <see cref="AfterLaunchBehavior"/> 对应）。</summary>
    public int AfterLaunchIndex
    {
        get => _afterLaunchIndex;
        set
        {
            if (value < 0 || value > 1)
            {
                return;
            }

            if (SetProperty(ref _afterLaunchIndex, value))
            {
                SaveNow();
            }
        }
    }

    public AfterLaunchBehavior CurrentAfterLaunch => _afterLaunchIndex == 1 ? AfterLaunchBehavior.Minimize : AfterLaunchBehavior.Keep;

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

    public string VersionText { get; } = "版本 " + (
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "未知");

    /// <summary>打开目录 / 导出诊断包的结果提示（成功给出位置，失败给出原因）。</summary>
    public string ToolMessage
    {
        get => _toolMessage;
        private set
        {
            if (SetProperty(ref _toolMessage, value))
            {
                OnPropertyChanged(nameof(HasToolMessage));
            }
        }
    }

    public bool HasToolMessage => ToolMessage.Length > 0;

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            ToolMessage = string.Empty;
        }
        catch (Exception ex)
        {
            DesktopLog.Write($"打开目录失败：{path}", ex);
            ToolMessage = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>导出脱敏诊断包（仅日志与环境信息，不含 config.json）到数据目录下的 diagnostics，并打开所在位置。</summary>
    private void ExportDiagnostics()
    {
        try
        {
            var target = Path.Combine(AppPaths.DataRoot, "diagnostics", $"diag-{DateTime.Now:yyyyMMdd-HHmmss}");
            DiagnosticExporter.Export(AppPaths.DataRoot, target);
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            ToolMessage = "已导出脱敏诊断包";
        }
        catch (Exception ex)
        {
            DesktopLog.Write("导出诊断包失败", ex);
            ToolMessage = $"导出诊断包失败：{ex.Message}";
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
            _data.Preferences.AfterLaunch = CurrentAfterLaunch.ToString();
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
