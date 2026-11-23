using Sparxie.Infrastructure.Processes;
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
    private GameKey _selectedGame;
    private GameProfile? _selectedProfile;
    private string _statusMessage = "准备就绪";
    private bool _isLaunching;
    private readonly HashSet<GameKey> _activeGames = new();

    /// <summary>
    /// 当前页面游戏的进程监视：检测只绑定到当前选中的游戏（<see cref="SelectedGame"/>），换游戏就换盯的进程；
    /// 以后增加游戏，只要它在 GameExecutables 里登记了进程名就自动适用，不用再改这里。
    /// </summary>
    private GameKey? _watchedGame;

    private System.Diagnostics.Process? _watchedProcess;

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private int _refreshing;
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
        try
        {
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcher = null; // 没有 UI 线程（单元测试）：不检测进程
        }

        _ = RefreshGameProcessAsync();
    }

    /// <summary>
    /// 检测<b>当前页面游戏</b>是不是已经在运行（启动器重开后仍在运行的、被别的启动器启动的）。不轮询：
    /// 只在启动器启动、切换游戏、窗口被激活、点启动之前各检测一次；发现后订阅这个进程的退出事件，退出时自动恢复，和 Starward 的做法一致。
    /// </summary>
    public async Task RefreshGameProcessAsync()
    {
        if (_dispatcher is null || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            GameKey game;
            do
            {
                game = SelectedGame;
                if (_watchedGame is { } watching && watching != game)
                {
                    StopWatching(); // 换了游戏：放掉之前盯的进程
                    RaiseLaunchState();
                }

                var processes = await Task.Run(() => GameProcessDetector.FindRunning(GameDirectory.ExeNames(game.Id))).ConfigureAwait(true);
                if (SelectedGame != game)
                {
                    DisposeAll(processes); // 检测期间切了游戏：丢弃结果，下一轮重新检测
                    continue;
                }

                if (processes.Length == 0)
                {
                    if (StopWatching())
                    {
                        RaiseLaunchState();
                    }

                    continue;
                }

                if (_watchedProcess is { } existing && _watchedGame == game && !existing.HasExited)
                {
                    DisposeAll(processes);
                    continue;
                }

                StopWatching();
                Watch(game, processes);
                RaiseLaunchState();
            }
            while (SelectedGame != game);
        }
        catch
        {
            // 检测失败时保持上一次的结果。
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void Watch(GameKey game, System.Diagnostics.Process[] processes)
    {
        var watched = processes[0];
        DisposeAll(processes.Skip(1));
        _watchedGame = game;
        _watchedProcess = watched;
        try
        {
            watched.EnableRaisingEvents = true;
            watched.Exited += (_, _) => _dispatcher?.TryEnqueue(() =>
            {
                if (ReferenceEquals(_watchedProcess, watched))
                {
                    StopWatching();
                    RaiseLaunchState();
                }
            });
            if (watched.HasExited)
            {
                StopWatching();
            }
        }
        catch
        {
            // 管理员权限的游戏进程，普通权限的启动器打不开它的句柄，订阅不了 Exited（实测会检测不到退出）。
            // 退回只盯这一个游戏、每秒按进程名确认还在不在（进程枚举不需要打开句柄），游戏退出或换游戏就停。
            StartExitFallback(game, watched);
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _exitFallback;

    private void StartExitFallback(GameKey game, System.Diagnostics.Process watched)
    {
        if (_dispatcher is null)
        {
            return;
        }

        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        var checking = 0;
        timer.Tick += async (_, _) =>
        {
            if (!ReferenceEquals(_watchedProcess, watched))
            {
                timer.Stop();
                return;
            }

            if (Interlocked.Exchange(ref checking, 1) == 1)
            {
                return;
            }

            try
            {
                var stillRunning = await Task.Run(() =>
                {
                    var found = GameProcessDetector.FindRunning(GameDirectory.ExeNames(game.Id));
                    DisposeAll(found);
                    return found.Length > 0;
                }).ConfigureAwait(true);
                if (!stillRunning && ReferenceEquals(_watchedProcess, watched))
                {
                    timer.Stop();
                    StopWatching();
                    RaiseLaunchState();
                }
            }
            catch
            {
                // 这一轮检测失败，下一秒再试。
            }
            finally
            {
                Interlocked.Exchange(ref checking, 0);
            }
        };
        _exitFallback = timer;
        timer.Start();
    }

    private bool StopWatching()
    {
        _exitFallback?.Stop();
        _exitFallback = null;
        var process = _watchedProcess;
        _watchedProcess = null;
        _watchedGame = null;
        process?.Dispose();
        return process is not null;
    }

    private static void DisposeAll(IEnumerable<System.Diagnostics.Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; }

    public IReadOnlyList<GameKey> Games => GameDirectory.Games;

    /// <summary>左下游戏卡 Flyout 的内容：三个游戏，多配置的游戏下挂配置 Chip。</summary>
    public ObservableCollection<GameEntryViewModel> GameEntries { get; }

    public ParameterRelayCommand SelectGameCommand { get; }

    public ParameterRelayCommand SelectProfileCommand { get; }

    public AsyncRelayCommand LaunchCommand { get; }

    /// <summary>某个游戏会话进入 Running（注入成功、游戏已在运行）时触发，用于“启动后窗口行为”。</summary>
    public event Action? GameRunning;

    public GameKey SelectedGame
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
            _ = RefreshGameProcessAsync();
        }
    }

    private void PersistSelectedGame(GameKey game)
    {
        try
        {
            _data.Preferences.SelectedGame = game.Id;
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

    /// <summary>当前游戏所有官方背景的圆点（主页底部居中切换）；只有一张时不显示。</summary>
    public ObservableCollection<BackgroundDotViewModel> BackgroundDots { get; } = new();

    public bool HasMultipleBackgrounds => BackgroundDots.Count > 1;

    /// <summary>底部切换条有内容可显示：背景不止一张，或当前是动态背景（要暂停键）。</summary>
    public bool ShowBackgroundControls => HasMultipleBackgrounds || HasBackgroundVideo;

    private IReadOnlyList<GameBackgroundItem> _backgroundItems = [];
    private int _backgroundIndex = -1;
    private string? _backgroundVideoPath;
    private bool _backgroundPaused;

    private string? _backgroundThemePath;

    /// <summary>当前背景的标题叠加图（透明 PNG，游戏标题 / Logo），盖在图片和视频之上；没有则为 null。</summary>
    public string? BackgroundThemePath
    {
        get => _backgroundThemePath;
        private set => SetProperty(ref _backgroundThemePath, value);
    }

    /// <summary>当前背景的视频（动态背景）本地路径；静态背景或视频未下载好时为 null。</summary>
    public string? BackgroundVideoPath
    {
        get => _backgroundVideoPath;
        private set
        {
            if (SetProperty(ref _backgroundVideoPath, value))
            {
                OnPropertyChanged(nameof(HasBackgroundVideo));
            }
        }
    }

    /// <summary>当前背景是动态背景（有视频）：此时才显示暂停键。</summary>
    public bool HasBackgroundVideo => _backgroundIndex >= 0 && _backgroundIndex < _backgroundItems.Count
        && _backgroundItems[_backgroundIndex].HasVideo;

    /// <summary>用户暂停了动态背景（停在当前帧）；切换背景后保持这个状态。</summary>
    public bool IsBackgroundPaused
    {
        get => _backgroundPaused;
        private set
        {
            if (SetProperty(ref _backgroundPaused, value))
            {
                OnPropertyChanged(nameof(PauseGlyph));
                OnPropertyChanged(nameof(PauseAutomationName));
            }
        }
    }

    /// <summary>暂停键始终占位（静态背景时透明且点不到），这样在有 / 没有暂停键的背景之间切换，圆点不会被挤动。</summary>
    public double PauseButtonOpacity => HasBackgroundVideo ? 1 : 0;

    public string PauseGlyph => _backgroundPaused ? "\uE768" : "\uE769"; // 播放 / 暂停

    public string PauseAutomationName => _backgroundPaused ? "播放动态背景" : "暂停动态背景";

    public ParameterRelayCommand SelectBackgroundCommand => _selectBackgroundCommand ??= new ParameterRelayCommand(parameter =>
    {
        if (parameter is int index)
        {
            SelectBackground(index);
        }
        else if (parameter is string text && int.TryParse(text, out var parsed))
        {
            SelectBackground(parsed);
        }
    });

    private ParameterRelayCommand? _selectBackgroundCommand;

    public RelayCommand ToggleBackgroundPauseCommand => _toggleBackgroundPauseCommand ??= new RelayCommand(() => IsBackgroundPaused = !IsBackgroundPaused);

    private RelayCommand? _toggleBackgroundPauseCommand;

    private async Task LoadBackgroundAsync(GameKey game)
    {
        try
        {
            // 先立即显示该游戏上次的缓存背景，再后台检查官方是否换图。
            var cached = _backgrounds.GetCachedBackgrounds(game);
            if (cached.Count > 0 && SelectedGame == game)
            {
                ApplyBackgrounds(game, cached);
            }

            var items = await _backgrounds.GetBackgroundsAsync(game).ConfigureAwait(true);
            if (SelectedGame == game)
            {
                ApplyBackgrounds(game, items);
            }
        }
        catch
        {
            if (SelectedGame == game)
            {
                ApplyBackgrounds(game, []);
            }
        }
    }

    private void ApplyBackgrounds(GameKey game, IReadOnlyList<GameBackgroundItem> items)
    {
        _backgroundItems = items;
        BackgroundDots.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            BackgroundDots.Add(new BackgroundDotViewModel(i, $"背景 {i + 1}"));
        }

        OnPropertyChanged(nameof(HasMultipleBackgrounds));
        OnPropertyChanged(nameof(ShowBackgroundControls));
        OnPropertyChanged(nameof(PauseButtonOpacity));

        var picked = Services.GameBackgroundService.Pick(items, _backgrounds.GetSelectedId(game));
        ShowBackground(picked is null ? -1 : items.ToList().IndexOf(picked));
    }

    private void SelectBackground(int index)
    {
        if (index < 0 || index >= _backgroundItems.Count || index == _backgroundIndex)
        {
            return;
        }

        _backgrounds.SetSelectedId(SelectedGame, _backgroundItems[index].Id);
        ShowBackground(index);
    }

    private GameBackgroundItem? _shownBackground;

    private void ShowBackground(int index)
    {
        _backgroundIndex = index;
        for (var i = 0; i < BackgroundDots.Count; i++)
        {
            BackgroundDots[i].IsSelected = i == index;
        }

        var item = index >= 0 && index < _backgroundItems.Count ? _backgroundItems[index] : null;

        // 显示的还是同一张（回到主页时会重新取一遍背景列表）：什么都不重置，否则视频会从头重播，画面割裂。
        if (item is not null && _shownBackground is { } shown && shown.Id == item.Id && shown.ImagePath == item.ImagePath
            && shown.VideoUrl == item.VideoUrl && shown.ThemePath == item.ThemePath)
        {
            _shownBackground = item;
            OnPropertyChanged(nameof(HasBackgroundVideo));
            OnPropertyChanged(nameof(ShowBackgroundControls));
            OnPropertyChanged(nameof(PauseButtonOpacity));
            return;
        }

        _shownBackground = item;
        BackgroundPath = item?.ImagePath;
        BackgroundThemePath = item?.ThemePath;
        BackgroundVideoPath = null;
        OnPropertyChanged(nameof(HasBackgroundVideo));
        OnPropertyChanged(nameof(ShowBackgroundControls));
        OnPropertyChanged(nameof(PauseButtonOpacity));
        if (item is { HasVideo: true })
        {
            _ = LoadVideoAsync(item);
        }
    }

    /// <summary>视频较大，选中动态背景后在后台下载；下载好之前先显示静态图，期间用户切走就丢弃结果。</summary>
    private async Task LoadVideoAsync(GameBackgroundItem item)
    {
        try
        {
            var path = await _backgrounds.EnsureVideoAsync(item).ConfigureAwait(true);
            if (path is not null && _backgroundIndex >= 0 && _backgroundIndex < _backgroundItems.Count
                && ReferenceEquals(_backgroundItems[_backgroundIndex], item))
            {
                BackgroundVideoPath = path;
            }
        }
        catch
        {
            // 视频失败时保留静态图。
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
        : $"{FormatGame(new GameKey(SelectedProfile.Game))} · {SelectedProfile.ExecutablePath}";

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
    public bool IsSelectedGameActive => SelectedProfile is { } profile && (_activeGames.Contains(new GameKey(profile.Game)) || (_watchedProcess is not null && _watchedGame == new GameKey(profile.Game)));

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
            ExecutablePathIssue.Empty => $"还没有设置 {GameNames.Display(new GameKey(profile.Game))} 的游戏路径",
            ExecutablePathIssue.NotFound => $"找不到 {GameNames.Display(new GameKey(profile.Game))}，游戏可能已移动或卸载",
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

    private void SetGameActive(GameKey game, bool active)
    {
        if (active ? _activeGames.Add(game) : _activeGames.Remove(game))
        {
            RaiseLaunchState();
        }
    }

    /// <summary>该游戏当前可选（内置，或已确认的插件游戏）才返回；插件被停用/删除后落回别的游戏。</summary>
    private GameKey? AvailableGame(string id) => Games.Contains(new GameKey(id)) ? new GameKey(id) : null;

    public void Refresh()
    {
        var selected = _data.TryGetSelectedProfile();
        _selectedGame = RestoreSelectedGame()
            ?? (selected is { } chosen ? AvailableGame(chosen.Game) : null)
            ?? (_data.Config.Profiles.FirstOrDefault() is { } first ? AvailableGame(first.Game) : null)
            ?? (GameKey)Sparxie.Contracts.Models.GameType.Genshin;
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
    private GameKey? RestoreSelectedGame() =>
        GameIds.Normalize(_data.Preferences.SelectedGame) is { } saved && Games.Contains(new GameKey(saved)) ? new GameKey(saved) : null;

    /// <summary>该游戏第一个已填写且文件存在的 EXE 路径（插件没有自带图标时，用它的图标作游戏图标）。</summary>
    public string? FirstExecutablePath(GameKey game) =>
        _data.Config.Profiles
            .Where(item => item.Game == game.Id && !string.IsNullOrWhiteSpace(item.ExecutablePath))
            .Select(item => item.ExecutablePath)
            .FirstOrDefault(File.Exists);

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _data.Config.Profiles.Where(item => item.Game == SelectedGame.Id))
        {
            Profiles.Add(new ProfileListItemViewModel(profile));
        }
        OnPropertyChanged(nameof(HasMultipleProfiles));
        OnPropertyChanged(nameof(HasSingleProfile));
        OnPropertyChanged(nameof(CurrentGameTitle));
    }

    private void SelectGame(object? parameter)
    {
        if (parameter is string text && GameIds.IsValidId(text))
        {
            parameter = new GameKey(GameIds.Normalize(text)!);
        }

        if (parameter is GameKey game)
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
    private string? ResolveProfileId(GameKey game)
    {
        var inGame = _data.Config.Profiles.Where(item => item.Game == game.Id).ToList();
        if (TryGetRememberedProfile(game, out var saved)
            && inGame.Any(item => item.Id == saved))
        {
            return saved;
        }

        return inGame.FirstOrDefault()?.Id;
    }

    private bool TryGetRememberedProfile(GameKey game, out string? profileId) =>
        _data.Preferences.LastProfileByGame.TryGetValue(game.Id, out profileId)
        || (game.BuiltIn is { } legacyGame && _data.Preferences.LastProfileByGame.TryGetValue(legacyGame.ToString(), out profileId));

    private void RememberProfile(GameProfile? profile)
    {
        if (profile is null
            || (TryGetRememberedProfile(new GameKey(profile.Game), out var current) && current == profile.Id))
        {
            return;
        }

        // 旧版本以 PascalCase 名存键；写回统一用 id，并清掉旧键避免两份不一致。
        if (new GameKey(profile.Game).BuiltIn is { } legacy) { _data.Preferences.LastProfileByGame.Remove(legacy.ToString()); }
        _data.Preferences.LastProfileByGame[profile.Game] = profile.Id;
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
            var profiles = _data.Config.Profiles.Where(item => item.Game == game.Id).ToList();
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
        await RefreshGameProcessAsync().ConfigureAwait(true);
        if (IsSelectedGameActive)
        {
            return; // 游戏已经在运行（刚被别的启动器启动），按钮会变成“运行中”
        }

        IsLaunching = true;
        StatusMessage = $"正在启动 {profile.DisplayName}…";
        try
        {
            var session = await _launcher.StartAsync(profile).ConfigureAwait(true);
            StatusMessage = $"已启动 {profile.DisplayName}，会话 {session.SessionId[..Math.Min(8, session.SessionId.Length)]}";

            // 会话存续期间该游戏的启动按钮禁用并显示“运行中”，直到收到终态事件。
            SetGameActive(new GameKey(profile.Game), true);
            _ = ObserveSessionAsync(session, new GameKey(profile.Game));
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

    private async Task ObserveSessionAsync(LauncherSession session, GameKey game)
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

    private static string FormatGame(GameKey game) => GameNames.Display(game);
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

/// <summary>主页底部背景切换的一个圆点。</summary>
public sealed class BackgroundDotViewModel : ObservableObject
{
    private bool _isSelected;

    public BackgroundDotViewModel(int index, string automationName)
    {
        Index = index;
        AutomationName = automationName;
    }

    public int Index { get; }

    public string AutomationName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(DotOpacity));
            }
        }
    }

    public double DotOpacity => _isSelected ? 1.0 : 0.45;
}
