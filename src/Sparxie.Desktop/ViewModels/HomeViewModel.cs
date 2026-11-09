using System.Collections.ObjectModel;
using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.LauncherCore;

namespace Sparxie.Desktop.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly DesktopDataService _data;
    private readonly LauncherService _launcher;
    private readonly Services.GameBackgroundService _backgrounds = new();
    private GameType _selectedGame;
    private GameProfile? _selectedProfile;
    private string _statusMessage = "准备就绪";
    private bool _isLaunching;
    private string? _backgroundPath;

    public HomeViewModel(DesktopDataService data, LauncherService launcher)
    {
        _data = data;
        _launcher = launcher;
        Profiles = new ObservableCollection<ProfileListItemViewModel>();
        SelectGameCommand = new ParameterRelayCommand(SelectGame);
        SelectProfileCommand = new ParameterRelayCommand(SelectProfile);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => SelectedProfile is not null && !IsLaunching);
        Refresh();
    }

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; }

    public GameType[] Games { get; } = Enum.GetValues<GameType>();

    public ParameterRelayCommand SelectGameCommand { get; }

    public ParameterRelayCommand SelectProfileCommand { get; }

    public AsyncRelayCommand LaunchCommand { get; }

    public GameType SelectedGame
    {
        get => _selectedGame;
        private set
        {
            if (!SetProperty(ref _selectedGame, value))
            {
                return;
            }

            PersistSelectedGame(value);
            RefreshProfiles();
            _ = LoadBackgroundAsync(value);
        }
    }

    private void PersistSelectedGame(GameType game)
    {
        try
        {
            _data.Preferences.SelectedGame = game.ToString();
            _data.SavePreferences();
        }
        catch
        {
            // 偏好保存失败不影响界面切换。
        }
    }

    /// <summary>当前游戏的官方启动器背景图本地路径；null 表示回退纯色背景。</summary>
    public string? BackgroundPath
    {
        get => _backgroundPath;
        private set => SetProperty(ref _backgroundPath, value);
    }

    private async Task LoadBackgroundAsync(GameType game)
    {
        try
        {
            var path = await _backgrounds.GetBackgroundPathAsync(game).ConfigureAwait(true);
            if (SelectedGame == game)
            {
                BackgroundPath = path;
            }
        }
        catch
        {
            if (SelectedGame == game)
            {
                BackgroundPath = null;
            }
        }
    }

    public GameProfile? SelectedProfile
    {
        get => _selectedProfile;
        private set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CurrentProfileName));
            OnPropertyChanged(nameof(CurrentProfileDetails));
            LaunchCommand.NotifyCanExecuteChanged();
        }
    }

    public string CurrentProfileName => SelectedProfile?.DisplayName ?? "未选择配置";

    public string CurrentProfileDetails => SelectedProfile is null
        ? "请先在 Profile 管理中添加游戏配置"
        : $"{FormatGame(SelectedProfile.Game)} · {SelectedProfile.ExecutablePath}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (!SetProperty(ref _isLaunching, value))
            {
                return;
            }

            LaunchCommand.NotifyCanExecuteChanged();
        }
    }

    public void Refresh()
    {
        var selected = _data.TryGetSelectedProfile();
        _selectedGame = RestoreSelectedGame()
            ?? selected?.Game
            ?? _data.Config.Profiles.FirstOrDefault()?.Game
            ?? GameType.Genshin;
        OnPropertyChanged(nameof(SelectedGame));
        RefreshProfiles();

        // 按 ID 从刚重建的列表里取实例：域层保存是整卡替换，旧引用会与配置脱钩。
        SelectedProfile = Profiles
            .FirstOrDefault(item => string.Equals(item.Profile.Id, selected?.Id, StringComparison.Ordinal))
            ?.Profile
            ?? Profiles.FirstOrDefault()?.Profile;
        _ = LoadBackgroundAsync(SelectedGame);
    }

    /// <summary>恢复上次选中的游戏标签；无记录或值非法时回退到所选 Profile 的游戏。</summary>
    private GameType? RestoreSelectedGame() =>
        Enum.TryParse<GameType>(_data.Preferences.SelectedGame, out var saved) ? saved : null;

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _data.Config.Profiles.Where(item => item.Game == SelectedGame))
        {
            Profiles.Add(new ProfileListItemViewModel(profile));
        }
    }

    private void SelectGame(object? parameter)
    {
        if (parameter is GameType game)
        {
            SelectedGame = game;
            SelectedProfile = Profiles.FirstOrDefault()?.Profile;
        }
        else if (parameter is string text && Enum.TryParse<GameType>(text, out game))
        {
            SelectedGame = game;
            SelectedProfile = Profiles.FirstOrDefault()?.Profile;
        }
    }

    private void SelectProfile(object? parameter)
    {
        if (parameter is not ProfileListItemViewModel item)
        {
            return;
        }

        SelectedProfile = item.Profile;
        try
        {
            _data.SelectProfile(item.Profile.Id);
        }
        catch (Exception ex)
        {
            // 配置中存在校验不通过的 Profile 时保存会失败；此处只提示，不让主页崩溃。
            StatusMessage = $"记录所选配置失败：{ex.Message}";
            Services.DesktopLog.Write($"记录所选 Profile 失败（id={item.Profile.Id}）", ex);
        }
    }

    private async Task LaunchAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        IsLaunching = true;
        StatusMessage = $"正在启动 {SelectedProfile.DisplayName}…";
        try
        {
            var session = await _launcher.StartAsync(SelectedProfile).ConfigureAwait(true);
            StatusMessage = $"已启动 {SelectedProfile.DisplayName}，会话 {session.SessionId[..Math.Min(8, session.SessionId.Length)]}";
            _ = ObserveSessionAsync(session);
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动失败：{ex.Message}";
        }
        finally
        {
            IsLaunching = false;
        }
    }

    private async Task ObserveSessionAsync(LauncherSession session)
    {
        try
        {
            await foreach (var ev in session.Events.ConfigureAwait(true))
            {
                if (ev.State is "Exited" or "Failed" or "HostCrashedBeforeRunning" or "HostCrashedAfterRunning")
                {
                    StatusMessage = string.IsNullOrWhiteSpace(ev.Message)
                        ? $"会话结束：{ev.State}"
                        : $"会话结束：{ev.Message}";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"会话事件流中断：{ex.Message}";
        }
    }

    private static string FormatGame(GameType game) => game switch
    {
        GameType.Genshin => "原神",
        GameType.StarRail => "星穹铁道",
        GameType.ZenlessZoneZero => "绝区零",
        _ => game.ToString(),
    };
}

public sealed class ProfileListItemViewModel
{
    public ProfileListItemViewModel(GameProfile profile) => Profile = profile;

    public GameProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;

    public string Details => Profile.Hoyo is { } hoyo
        ? $"{hoyo.TargetFps} FPS · {Profile.ExecutablePath}"
        : Profile.ExecutablePath;
}
