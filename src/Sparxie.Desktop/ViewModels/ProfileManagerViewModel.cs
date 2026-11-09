using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.LauncherCore;

namespace Sparxie.Desktop.ViewModels;

public sealed class ProfileManagerViewModel : ObservableObject
{
    private readonly DesktopDataService _data;
    private ProfileEditorViewModel? _selectedProfile;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _isRebuilding;
    private GameType _scope = GameType.Genshin;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clearTimer;

    public ProfileManagerViewModel(DesktopDataService data)
    {
        _data = data;
        Profiles = new ObservableCollection<ProfileEditorViewModel>();
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMultiple));
            OnPropertyChanged(nameof(ShowProfileName));
            RaisePending();
        };
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        NewCommand = new RelayCommand(NewProfile);
        SaveCommand = new RelayCommand(Save, () => HasPendingChanges);
        DiscardCommand = new RelayCommand(Discard);
        DeleteCommand = new RelayCommand(Delete, () => SelectedProfile is not null);
        Refresh();
    }

    public ObservableCollection<ProfileEditorViewModel> Profiles { get; }

    public RelayCommand NewCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand DiscardCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>当前管理的游戏：配置按游戏分开管理，页面只展示该游戏的配置。</summary>
    public GameType Scope => _scope;

    public string ScopeTitle => $"{GameNames.Display(_scope)} 设置";

    /// <summary>该游戏有多个配置（如国服/国际服）时才显示列表；大多数人只有一个，直接是表单。</summary>
    public bool HasMultiple => Profiles.Count > 1;

    /// <summary>切换到某个游戏的配置页；该游戏还没有配置时自动落一个占位配置。</summary>
    public void SetScope(GameType game)
    {
        _scope = game;
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ScopeTitle));
        Profiles.Clear();
        _selectedProfile = null;
        EnsureDefaultIfEmpty(game);
        Refresh();
    }

    /// <summary>与列表 SelectedItem 双向绑定，保证界面选中项与编辑表单是同一真相源。</summary>
    public ProfileEditorViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            // 重建列表时 ListView 会因旧选中项不在新集合中而回写 null；
            // 此处忽略这类回写，避免选中态被清空后表单控件在空上下文里反向污染数据。
            if (_isRebuilding)
            {
                return;
            }

            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            SaveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ShowProfileName));
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    /// <summary>无选中项时隐藏整个详情表单，避免空 DataContext 下控件回写污染数据。</summary>
    public bool HasSelection => SelectedProfile is not null;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(ShowActionBar));
                OnPropertyChanged(nameof(ActionBarMessage));
            }
        }
    }

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    private void ReportInfo(string message)
    {
        StatusSeverity = InfoBarSeverity.Informational;
        StatusMessage = message;
    }

    private void ReportSuccess(string message)
    {
        StatusSeverity = InfoBarSeverity.Success;
        StatusMessage = message;
        ScheduleClearStatus();
    }

    private void ReportError(string message)
    {
        StatusSeverity = InfoBarSeverity.Error;
        StatusMessage = message;
    }

    /// <summary>进入 Profile 页时若一个 Profile 都没有，则自动落一个占位 Profile 供右侧补全。</summary>
    public void EnsureDefaultIfEmpty(GameType game)
    {
        if (_data.Config.Profiles.Any(item => item.Game == game))
        {
            return;
        }

        try
        {
            var taken = _data.Config.Profiles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var id = game.ToString().ToLowerInvariant();
            for (var n = 2; taken.Contains(id); n++)
            {
                id = $"{game.ToString().ToLowerInvariant()}-{n}";
            }

            _data.AddProfile(new ProfileMutation(Id: id, DisplayName: $"{GameNames.Display(game)} 配置", Game: game));
            ReportInfo("请先选择游戏 EXE 路径");
        }
        catch (Exception ex)
        {
            ReportError(Describe(ex, "自动创建 Profile 失败"));
            DesktopLog.Write("自动创建默认 Profile 失败", ex);
        }
    }

    /// <summary>
    /// 用配置重建列表。保留当前选中项与未保存的新建项，避免页面切换丢弃编辑中的内容。
    /// </summary>
    public void Refresh()
    {
        var pendingNew = Profiles.FirstOrDefault(item => item.IsNew);
        var previousId = SelectedProfile?.Id;
        var previousWasNew = SelectedProfile?.IsNew ?? false;

        _isRebuilding = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in _data.Config.Profiles.Where(item => item.Game == _scope))
            {
                Profiles.Add(Track(ProfileEditorViewModel.From(profile)));
            }

            if (pendingNew is not null)
            {
                Profiles.Add(pendingNew);
            }
        }
        finally
        {
            _isRebuilding = false;
        }

        var restored = previousWasNew
            ? pendingNew
            : Profiles.FirstOrDefault(item => !item.IsNew && string.Equals(item.Id, previousId, StringComparison.Ordinal))
                ?? Profiles.FirstOrDefault(item => string.Equals(item.Id, _data.Config.SelectedProfileId, StringComparison.Ordinal))
                ?? Profiles.FirstOrDefault();

        // 重建期间 ListView 已把 UI 选中态清空，而守卫阻止了它写入字段；
        // 这里无条件推一次通知，确保列表选中态与表单重新对上，不依赖引用是否恰好变化。
        _selectedProfile = restored;
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(HasSelection));
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void NewProfile()
    {
        var existing = Profiles.FirstOrDefault(item => item.IsNew);
        if (existing is not null)
        {
            SelectedProfile = existing;
            ReportInfo("已有一个未保存的新建 Profile，请先保存或修改它");
            return;
        }

        // 加入集合后左栏才会出现该条目，否则新建项不可见且切换即丢失。
        var created = ProfileEditorViewModel.New(_data.Config.Profiles.Select(profile => profile.Id), _scope);
        Profiles.Add(Track(created));
        SelectedProfile = created;
        ReportInfo("为同一游戏添加另一套配置（如另一个服务器），填写后保存");
    }

    private void Save()
    {
        var targets = Profiles.Where(item => item.IsNew || item.IsDirty).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var selectedWasNew = SelectedProfile?.IsNew ?? false;
        var selectedId = SelectedProfile?.Id;
        GameProfile? lastSaved = null;
        foreach (var editing in targets)
        {
            try
            {
                var mutation = editing.ToMutation();
                var saved = editing.IsNew
                    ? _data.AddProfile(mutation)
                    : _data.UpdateProfile(editing.Id, mutation);
                lastSaved = saved;
                if (editing.IsNew)
                {
                    Profiles.Remove(editing);
                    if (selectedWasNew && ReferenceEquals(editing, SelectedProfile))
                    {
                        selectedId = saved.Id;
                    }
                }
                else
                {
                    editing.IsDirty = false;
                }
            }
            catch (Exception ex)
            {
                // 出错的那一套留在列表里并选中，已成功的不受影响；错误一直显示到下次修改。
                SelectedProfile = editing;
                ReportError(Describe(ex, "保存失败"));
                DesktopLog.Write($"保存配置失败（id={editing.Id}）", ex);
                return;
            }
        }

        Refresh();
        SelectedProfile = Profiles.FirstOrDefault(item =>
            !item.IsNew && string.Equals(item.Id, selectedId, StringComparison.Ordinal))
            ?? Profiles.FirstOrDefault(item => !item.IsNew);
        if (lastSaved is not null && string.IsNullOrEmpty(lastSaved.ExecutablePath))
        {
            ReportInfo("已保存；尚未设置游戏路径，启动前需补全");
        }
        else
        {
            ReportSuccess("已保存");
        }
    }

    private void Discard()
    {
        foreach (var item in Profiles.Where(item => item.IsNew).ToList())
        {
            Profiles.Remove(item);
        }

        _selectedProfile = null;
        Refresh();
        StatusMessage = string.Empty;
    }

    private ProfileEditorViewModel Track(ProfileEditorViewModel editor)
    {
        editor.PropertyChanged += OnEditorChanged;
        return editor;
    }

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ProfileEditorViewModel editor)
        {
            return;
        }

        if (e.PropertyName is not (nameof(ProfileEditorViewModel.IsDirty) or nameof(ProfileEditorViewModel.IsNew)))
        {
            editor.IsDirty = true;

            // 用户继续编辑后，上一次的保存结果/错误不再适用。
            if (HasStatus)
            {
                StatusMessage = string.Empty;
            }
        }

        RaisePending();
    }

    private void RaisePending()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(ShowActionBar));
        OnPropertyChanged(nameof(ActionBarMessage));
        OnPropertyChanged(nameof(ShowProfileName));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public bool HasPendingChanges => Profiles.Any(item => item.IsNew || item.IsDirty);

    /// <summary>单配置时不展示名称；多配置或正在新建时展示，用来区分各套配置。</summary>
    public bool ShowProfileName => HasMultiple || (SelectedProfile?.IsNew ?? false);

    public bool ShowActionBar => HasPendingChanges || HasStatus;

    public string ActionBarMessage => HasStatus ? StatusMessage : "有未保存的修改";

    /// <summary>保存成功的提示几秒后自动消失；错误提示保留到用户再次修改。</summary>
    private void ScheduleClearStatus()
    {
        if (_dispatcher is null)
        {
            return;
        }

        _clearTimer?.Stop();
        _clearTimer = _dispatcher.CreateTimer();
        _clearTimer.Interval = TimeSpan.FromSeconds(3);
        _clearTimer.IsRepeating = false;
        _clearTimer.Tick += (_, _) =>
        {
            if (StatusSeverity == InfoBarSeverity.Success)
            {
                StatusMessage = string.Empty;
            }
        };
        _clearTimer.Start();
    }

    private void Delete()
    {
        if (SelectedProfile is not { } editing)
        {
            return;
        }

        if (editing.IsNew)
        {
            // 未保存的新建项直接丢弃，无需触碰配置。
            Profiles.Remove(editing);
            SelectedProfile = Profiles.FirstOrDefault();
            ReportInfo("已放弃未保存的新建 Profile");
            return;
        }

        try
        {
            _data.RemoveProfile(editing.Id);
            Refresh();
            ReportSuccess($"已删除 {editing.DisplayName}");
        }
        catch (Exception ex)
        {
            ReportError(Describe(ex, "删除失败"));
            DesktopLog.Write($"删除 Profile 失败（id={editing.Id}）", ex);
        }
    }

    /// <summary>把域层与配置层异常翻译成用户能据此行动的文案，而不是只回显被截断的原始消息。</summary>
    private static string Describe(Exception exception, string prefix) => exception switch
    {
        LauncherException launcher => $"{prefix}：{launcher.Message}",
        InvalidOperationException invalid => $"{prefix}：配置校验未通过。{invalid.Message}",
        _ => $"{prefix}：{exception.Message}（详情见 {DesktopLog.Path}）",
    };
}

public sealed class ProfileEditorViewModel : ObservableObject
{
    private bool _isDirty;

    /// <summary>构造完成后任何可编辑字段变化即为 true；保存或重建后清除。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    private string _id = string.Empty;
    private string _displayName = string.Empty;
    private GameType _game;
    private string _executablePath = string.Empty;
    private bool _fpsUnlockEnabled = true;
    private double _targetFps = 120;
    private bool _backgroundFpsLimitEnabled = true;
    private double _backgroundFps = 10;
    private ProcessPriority _processPriority;
    private bool _genshinFollowInGamePreset;
    private double _genshinPreset30Fps = 60;
    private double _genshinPreset60Fps = 1000;
    private bool _genshinTouchUiScaleOverrideEnabled;
    private double _genshinTouchUiScalePercent = 400;

    public bool IsNew { get; private init; }

    // 必须是所有编辑项共享的同一实例：若每个 Profile 各持一份数组，切换 Profile 时
    // ComboBox 的 ItemsSource 会换成新集合，导致原 SelectedItem 不在集合中而被置空，
    // 再经 TwoWay 反向写回，表现为游戏类型/优先级下拉框漂移。
    public static GameType[] GameOptions { get; } = Enum.GetValues<GameType>();

    public static ProcessPriority[] PriorityOptions { get; } = Enum.GetValues<ProcessPriority>();

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public GameType Game
    {
        get => _game;
        set
        {
            if (!SetProperty(ref _game, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsHoyoGame));
            OnPropertyChanged(nameof(IsGenshin));
        }
    }
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value); }
    public bool FpsUnlockEnabled { get => _fpsUnlockEnabled; set => SetProperty(ref _fpsUnlockEnabled, value); }
    public double TargetFps { get => _targetFps; set => SetProperty(ref _targetFps, value); }
    public bool BackgroundFpsLimitEnabled { get => _backgroundFpsLimitEnabled; set => SetProperty(ref _backgroundFpsLimitEnabled, value); }
    public double BackgroundFps { get => _backgroundFps; set => SetProperty(ref _backgroundFps, value); }
    public ProcessPriority ProcessPriority { get => _processPriority; set => SetProperty(ref _processPriority, value); }
    public bool GenshinFollowInGamePreset { get => _genshinFollowInGamePreset; set => SetProperty(ref _genshinFollowInGamePreset, value); }
    public double GenshinPreset30Fps { get => _genshinPreset30Fps; set => SetProperty(ref _genshinPreset30Fps, value); }
    public double GenshinPreset60Fps { get => _genshinPreset60Fps; set => SetProperty(ref _genshinPreset60Fps, value); }
    public bool GenshinTouchUiScaleOverrideEnabled { get => _genshinTouchUiScaleOverrideEnabled; set => SetProperty(ref _genshinTouchUiScaleOverrideEnabled, value); }
    public double GenshinTouchUiScalePercent { get => _genshinTouchUiScalePercent; set => SetProperty(ref _genshinTouchUiScalePercent, value); }
    public bool IsHoyoGame => Game != GameType.ZenlessZoneZero;
    public bool IsGenshin => Game == GameType.Genshin;

    /// <summary>新建编辑项。ID 与名称预填可用默认值，避免「必填但界面无提示」导致保存必然失败。</summary>
    public static ProfileEditorViewModel New(IEnumerable<string> existingIds, GameType game)
    {
        var taken = new HashSet<string>(existingIds, StringComparer.Ordinal);
        var suffix = 1;
        var id = "profile-1";
        while (taken.Contains(id))
        {
            suffix++;
            id = $"profile-{suffix}";
        }

        return new ProfileEditorViewModel
        {
            IsNew = true,
            Game = game,
            Id = id,
            DisplayName = suffix == 1 ? $"{GameNames.Display(game)} 新配置" : $"{GameNames.Display(game)} 新配置 {suffix}",
        };
    }

    public static ProfileEditorViewModel From(GameProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        Game = profile.Game,
        ExecutablePath = profile.ExecutablePath,
        FpsUnlockEnabled = profile.Hoyo?.FpsUnlockEnabled ?? true,
        TargetFps = profile.Hoyo?.TargetFps ?? 120,
        BackgroundFpsLimitEnabled = profile.Hoyo?.BackgroundFpsLimitEnabled ?? true,
        BackgroundFps = profile.Hoyo?.BackgroundFps ?? 10,
        ProcessPriority = profile.Hoyo?.ProcessPriority ?? ProcessPriority.Normal,
        GenshinFollowInGamePreset = profile.Hoyo?.GenshinFollowInGamePreset ?? false,
        GenshinPreset30Fps = profile.Hoyo?.GenshinPreset30Fps ?? 60,
        GenshinPreset60Fps = profile.Hoyo?.GenshinPreset60Fps ?? 1000,
        GenshinTouchUiScaleOverrideEnabled = profile.Hoyo?.GenshinTouchUiScaleOverrideEnabled ?? false,
        GenshinTouchUiScalePercent = profile.Hoyo?.GenshinTouchUiScalePercent ?? 400,
    };

    /// <summary>
    /// 转为域层 mutation。空白路径必须传 null 而非空串：域层用 null 表达「本次不修改该字段」，
    /// 传空串会被理解成「把路径改成空」，导致占位 Profile 每次保存都失败。
    /// </summary>
    public ProfileMutation ToMutation() => new(
        Id: IsNew ? Id : null,
        DisplayName: DisplayName,
        Game: Game,
        ExecutablePath: string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
        FpsUnlockEnabled: IsHoyoGame ? FpsUnlockEnabled : null,
        TargetFps: IsHoyoGame ? ToFps(TargetFps, 120) : null,
        BackgroundFpsLimitEnabled: IsHoyoGame ? BackgroundFpsLimitEnabled : null,
        BackgroundFps: IsHoyoGame ? ToFps(BackgroundFps, 10) : null,
        ProcessPriority: IsHoyoGame ? ProcessPriority : null,
        GenshinFollowInGamePreset: IsGenshin ? GenshinFollowInGamePreset : null,
        GenshinPreset30Fps: IsGenshin ? ToFps(GenshinPreset30Fps, 60) : null,
        GenshinPreset60Fps: IsGenshin ? ToFps(GenshinPreset60Fps, 1000) : null,
        GenshinTouchUiScaleOverrideEnabled: IsGenshin ? GenshinTouchUiScaleOverrideEnabled : null,
        GenshinTouchUiScalePercent: IsGenshin ? ToFps(GenshinTouchUiScalePercent, 400) : null);

    /// <summary>NumberBox 被清空时值为 NaN，直接 Convert.ToInt32 会抛 OverflowException。</summary>
    private static int ToFps(double value, int fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : Convert.ToInt32(value);
}
