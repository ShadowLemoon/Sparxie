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
    private readonly HashSet<GameType> _activeGames = new();
    private string? _backgroundPath;

    public HomeViewModel(DesktopDataService data, LauncherService launcher)
    {
        _data = data;
        _launcher = launcher;
        Profiles = new ObservableCollection<ProfileListItemViewModel>();
        GameEntries = new ObservableCollection<GameEntryViewModel>();
        SelectGameCommand = new ParameterRelayCommand(SelectGame);
        SelectProfileCommand = new ParameterRelayCommand(SelectProfile);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => SelectedProfile is not null && !IsLaunching && !IsSelectedGameActive);
        Refresh();
    }

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; }

    public GameType[] Games { get; } = Enum.GetValues<GameType>();

    /// <summary>左下游戏卡 Flyout 的内容：三个游戏，多配置的游戏下挂配置 Chip。</summary>
    public ObservableCollection<GameEntryViewModel> GameEntries { get; }

    public ParameterRelayCommand SelectGameCommand { get; }

    public ParameterRelayCommand SelectProfileCommand { get; }

    public AsyncRelayCommand LaunchCommand { get; }

    /// <summary>某个游戏会话进入 Running（注入成功、游戏已在运行）时触发，用于“启动后窗口行为”。</summary>
    public event Action? GameRunning;

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
            RebuildEntries();
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
            // 先立即显示该游戏上次的缓存背景，再后台检查官方是否换图。
            if (_backgrounds.GetCachedBackgroundPath(game) is { } cached && SelectedGame == game)
            {
                BackgroundPath = cached;
            }

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
            RaiseLaunchState();
            RememberProfile(value);

            RebuildEntries();
        }
    }

    /// <summary>该游戏有多个配置（如国服/国际服）时主页才提供配置下拉；否则直接是“游戏设置”入口。</summary>
    public bool HasMultipleProfiles => Profiles.Count > 1;

    public bool HasSingleProfile => Profiles.Count <= 1;

    public string CurrentGameTitle => $"{GameNames.Display(SelectedGame)} 设置";

    public string CurrentProfileName => SelectedProfile?.DisplayName ?? "未选择配置";

    public string CurrentProfileDetails => SelectedProfile is null
        ? "尚未设置游戏路径，点击设置"
        : $"{FormatGame(SelectedProfile.Game)} · {SelectedProfile.ExecutablePath}";

    /// <summary>主页状态条：仅调试模式下显示（设置里开启，或环境变量 SPARXIE_DEBUG=1）；平时隐藏，失败原因仍写入 desktop.log。</summary>
    public bool HasStatusMessage => IsDebugMode && !string.IsNullOrWhiteSpace(StatusMessage) && StatusMessage != "准备就绪";

    public bool IsDebugMode =>
        _data.Preferences.DebugMode || Environment.GetEnvironmentVariable("SPARXIE_DEBUG") == "1";

    public void NotifyDebugModeChanged() => OnPropertyChanged(nameof(HasStatusMessage));

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
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

            RaiseLaunchState();
        }
    }

    /// <summary>当前选中的游戏已有会话在运行（同款游戏同一时间只允许一个会话）。</summary>
    public bool IsSelectedGameActive => SelectedProfile is { } profile && _activeGames.Contains(profile.Game);

    /// <summary>启动按钮文字：启动中 / 运行中 / 启动游戏。</summary>
    public string LaunchLabel => IsLaunching ? "启动中…" : IsSelectedGameActive ? "运行中" : "启动游戏";

    /// <summary>当前配置的游戏 EXE 路径问题；有问题时主页提示并提供「定位游戏」入口。</summary>
    public ExecutablePathIssue PathIssue => SelectedProfile is { } profile
        ? ExecutablePathCheck.Check(profile.Game, profile.ExecutablePath)
        : ExecutablePathIssue.None;

    public bool HasPathIssue => PathIssue != ExecutablePathIssue.None;

    public string PathIssueMessage => SelectedProfile is { } profile
        ? PathIssue switch
        {
            ExecutablePathIssue.Empty => $"还没有设置 {GameNames.Display(profile.Game)} 的游戏路径",
            ExecutablePathIssue.NotFound => $"找不到 {GameNames.Display(profile.Game)}，游戏可能已移动或卸载",
            var issue => ExecutablePathCheck.Describe(profile.Game, issue),
        }
        : string.Empty;

    private void RaiseLaunchState()
    {
        OnPropertyChanged(nameof(SwitcherText));
        OnPropertyChanged(nameof(SwitcherLabel));
        OnPropertyChanged(nameof(PathIssue));
        OnPropertyChanged(nameof(HasPathIssue));
        OnPropertyChanged(nameof(PathIssueMessage));
        OnPropertyChanged(nameof(IsSelectedGameActive));
        OnPropertyChanged(nameof(LaunchLabel));
        OnPropertyChanged(nameof(LaunchAutomationName));
        LaunchCommand.NotifyCanExecuteChanged();
    }

    private void SetGameActive(GameType game, bool active)
    {
        if (active ? _activeGames.Add(game) : _activeGames.Remove(game))
        {
            RaiseLaunchState();
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
        RebuildEntries();
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
        OnPropertyChanged(nameof(HasMultipleProfiles));
        OnPropertyChanged(nameof(HasSingleProfile));
        OnPropertyChanged(nameof(CurrentGameTitle));
    }

    private void SelectGame(object? parameter)
    {
        if (parameter is string text && Enum.TryParse<GameType>(text, out var parsed))
        {
            parameter = parsed;
        }

        if (parameter is GameType game)
        {
            SelectedGame = game;
            // 点游戏标题 = 该游戏“记住的配置”（Flyout 里已高亮显示），而不是随机一个。
            var targetId = ResolveProfileId(game);
            SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == targetId)?.Profile
                ?? Profiles.FirstOrDefault()?.Profile;
        }
    }

    private void SelectProfile(object? parameter)
    {
        if (parameter is ProfileListItemViewModel item)
        {
            ApplyProfile(item.Profile);
        }
    }

    private void ApplyProfile(GameProfile profile)
    {
        SelectedProfile = profile;
        try
        {
            _data.SelectProfile(profile.Id);
        }
        catch (Exception ex)
        {
            // 配置中存在校验不通过的 Profile 时保存会失败；此处只提示，不让主页崩溃。
            StatusMessage = $"记录所选配置失败：{ex.Message}";
            Services.DesktopLog.Write($"记录所选 Profile 失败（id={profile.Id}）", ex);
        }
    }

    /// <summary>某游戏点标题时会用到的配置：偏好里记住的 → 否则该游戏第一个。</summary>
    private string? ResolveProfileId(GameType game)
    {
        var inGame = _data.Config.Profiles.Where(item => item.Game == game).ToList();
        if (_data.Preferences.LastProfileByGame.TryGetValue(game.ToString(), out var saved)
            && inGame.Any(item => item.Id == saved))
        {
            return saved;
        }

        return inGame.FirstOrDefault()?.Id;
    }

    private void RememberProfile(GameProfile? profile)
    {
        if (profile is null
            || (_data.Preferences.LastProfileByGame.TryGetValue(profile.Game.ToString(), out var current) && current == profile.Id))
        {
            return;
        }

        _data.Preferences.LastProfileByGame[profile.Game.ToString()] = profile.Id;
        try
        {
            _data.SavePreferences();
        }
        catch
        {
            // 偏好保存失败不影响切换。
        }
    }

    /// <summary>左下游戏卡的完整名称（读屏用）：多配置时“原神 · 国服”，单配置只有游戏名。</summary>
    public string SwitcherLabel => HasMultipleProfiles && SelectedProfile is { } profile
        ? $"{GameNames.Display(SelectedGame)} · {profile.DisplayName}"
        : GameNames.Display(SelectedGame);

    /// <summary>游戏卡上显示的文字：图标已标识游戏，多配置时只显示配置名（名字太长会被截断），单配置显示游戏名。</summary>
    public string SwitcherText => HasMultipleProfiles && SelectedProfile is { } profile
        ? profile.DisplayName
        : GameNames.Display(SelectedGame);

    public Microsoft.UI.Xaml.Media.ImageSource? CurrentGameIcon => GameTabBinding.Icon(SelectedGame);

    public string SwitcherAutomationName => $"当前：{SwitcherLabel}。切换游戏或配置";

    public string GameSettingsAutomationName => $"{GameNames.Display(SelectedGame)} 游戏设置";

    public string LaunchAutomationName => IsSelectedGameActive ? $"{SwitcherLabel} 运行中" : $"启动 {SwitcherLabel}";

    /// <summary>游戏图标是异步下载的，加载完后补发属性变更并重建 Flyout 内容。</summary>
    public void NotifyGameIconsLoaded() => RebuildEntries();

    private void RebuildEntries()
    {
        GameEntries.Clear();
        foreach (var game in Games)
        {
            var profiles = _data.Config.Profiles.Where(item => item.Game == game).ToList();
            var chips = profiles
                .Select(profile => new ProfileChipViewModel(
                    profile,
                    game == SelectedGame && SelectedProfile?.Id == profile.Id,
                    profile.Id == ResolveProfileId(game),
                    () =>
                    {
                        SelectedGame = game;
                        ApplyProfile(profile);
                    }))
                .ToList();
            GameEntries.Add(new GameEntryViewModel(game, game == SelectedGame, chips, () => SelectGame(game)));
        }

        OnPropertyChanged(nameof(SwitcherLabel));
        OnPropertyChanged(nameof(SwitcherText));
        OnPropertyChanged(nameof(CurrentGameIcon));
        OnPropertyChanged(nameof(SwitcherAutomationName));
        OnPropertyChanged(nameof(GameSettingsAutomationName));
        OnPropertyChanged(nameof(LaunchAutomationName));
        RaiseLaunchState();
    }

    private async Task LaunchAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var profile = SelectedProfile;
        IsLaunching = true;
        StatusMessage = $"正在启动 {profile.DisplayName}…";
        try
        {
            var session = await _launcher.StartAsync(profile).ConfigureAwait(true);
            StatusMessage = $"已启动 {profile.DisplayName}，会话 {session.SessionId[..Math.Min(8, session.SessionId.Length)]}";

            // 会话存续期间该游戏的启动按钮禁用并显示“运行中”，直到收到终态事件。
            SetGameActive(profile.Game, true);
            _ = ObserveSessionAsync(session, profile.Game);
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

    private async Task ObserveSessionAsync(LauncherSession session, GameType game)
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

                if (ev.State == "Running")
                {
                    GameRunning?.Invoke();
                }

                // 非终态事件也展示：阶段（启动中/注入中/Running…）与消息，方便看到注入卡在哪一步。
                StatusMessage = string.IsNullOrWhiteSpace(ev.Message)
                    ? $"{ev.Stage} {ev.State}".Trim()
                    : $"{ev.Stage} {ev.State}：{ev.Message}".Trim();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"会话事件流中断：{ex.Message}";
        }
        finally
        {
            // 终态事件、事件流中断或结束，都不再认为该游戏在运行。
            SetGameActive(game, false);
        }
    }

    private static string FormatGame(GameType game) => GameNames.Display(game);
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
