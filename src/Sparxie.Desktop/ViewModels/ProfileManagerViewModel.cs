using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Sparxie.Contracts.Models;
using Sparxie.Desktop.Services;
using Sparxie.Infrastructure.Processes;
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
    private readonly IGameFinderEnvironment _finderEnvironment;
    private readonly IGameRegistry? _registry;

    public ProfileManagerViewModel(DesktopDataService data, IGameFinderEnvironment? finderEnvironment = null, IGameRegistry? registry = null)
    {
        _data = data;
        _finderEnvironment = finderEnvironment ?? new SystemGameFinderEnvironment();
        _registry = registry ?? (finderEnvironment is null ? new CurrentUserGameRegistry() : null);
        Profiles = new ObservableCollection<ProfileEditorViewModel>();
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMultiple));
            OnPropertyChanged(nameof(ShowProfileName));
        };
        _dispatcher = TryGetDispatcher();
        NewCommand = new RelayCommand(NewProfile);
        CopyCommand = new RelayCommand(CopyProfile, () => SelectedProfile is not null);
        DeleteCommand = new RelayCommand(Delete, () => SelectedProfile is not null);
        Refresh();
    }

    /// <summary>取 UI 线程的调度队列；没有 WinUI 运行环境（如单元测试）时返回 null，此时自动保存退化为立即保存。</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? TryGetDispatcher()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public ObservableCollection<ProfileEditorViewModel> Profiles { get; }

    public RelayCommand NewCommand { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>当前管理的游戏：配置按游戏分开管理，页面只展示该游戏的配置。</summary>
    public GameType Scope => _scope;

    public string ScopeTitle => $"{GameNames.Display(_scope)} 设置";

    /// <summary>该游戏有多个配置（如国服/国际服）时才显示列表；大多数人只有一个，直接是表单。</summary>
    public bool HasMultiple => Profiles.Count > 1;

    /// <summary>切换到某个游戏的配置页；该游戏还没有配置时自动落一个占位配置。</summary>
    public void SetScope(GameType game)
    {
        Flush();
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

            DeleteCommand.NotifyCanExecuteChanged();
            CopyCommand.NotifyCanExecuteChanged();
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

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clearTimer;

    /// <summary>保存成功的轻提示，2 秒后自动消失；错误提示保留到下次保存成功。</summary>
    private void ReportSuccess(string message, TimeSpan? duration = null)
    {
        StatusSeverity = InfoBarSeverity.Success;
        StatusMessage = message;
        if (_dispatcher is null)
        {
            return;
        }

        _clearTimer ??= _dispatcher.CreateTimer();
        _clearTimer.Stop();
        _clearTimer.Interval = duration ?? TimeSpan.FromSeconds(2);
        _clearTimer.IsRepeating = false;
        _clearTimer.Tick -= ClearSuccess;
        _clearTimer.Tick += ClearSuccess;
        _clearTimer.Start();
    }

    private void ClearSuccess(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (StatusSeverity == InfoBarSeverity.Success)
        {
            StatusMessage = string.Empty;
        }
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

            var names = _data.Config.Profiles.Where(item => item.Game == game).Select(item => item.DisplayName);
            _data.AddProfile(new ProfileMutation(Id: id, DisplayName: ProfileNames.NextDefault(names), Game: game));
            ReportInfo("请先选择游戏 EXE 路径");
        }
        catch (Exception ex)
        {
            ReportError(Describe(ex, "自动创建 Profile 失败"));
            DesktopLog.Write("自动创建默认 Profile 失败", ex);
        }
    }

    /// <summary>
    /// 用配置重建列表（先落盘尚未保存的编辑），保持当前选中项。
    /// </summary>
    public void Refresh()
    {
        Flush();
        var previousId = SelectedProfile?.Id;

        _isRebuilding = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in _data.Config.Profiles.Where(item => item.Game == _scope))
            {
                Profiles.Add(Track(ProfileEditorViewModel.From(profile)));
            }
        }
        finally
        {
            _isRebuilding = false;
        }

        var restored = Profiles.FirstOrDefault(item => string.Equals(item.Id, previousId, StringComparison.Ordinal))
            ?? Profiles.FirstOrDefault(item => string.Equals(item.Id, _data.Config.SelectedProfileId, StringComparison.Ordinal))
            ?? Profiles.FirstOrDefault();

        // 重建期间 ListView 已把 UI 选中态清空，而守卫阻止了它写入字段；
        // 这里无条件推一次通知，确保列表选中态与表单重新对上，不依赖引用是否恰好变化。
        _selectedProfile = restored;
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(HasSelection));
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>为当前游戏立即新建并保存另一套配置（如另一个服务器），随后在表单里补全。</summary>
    private void NewProfile()
    {
        Flush();
        try
        {
            var created = ProfileEditorViewModel.New(
                _data.Config.Profiles.Select(profile => profile.Id),
                _scope,
                _data.Config.Profiles.Where(profile => profile.Game == _scope).Select(profile => profile.DisplayName));
            var saved = _data.AddProfile(created.ToMutation());
            StatusMessage = string.Empty;
            _selectedProfile = null;
            Refresh();
            SelectedProfile = Profiles.FirstOrDefault(item => string.Equals(item.Id, saved.Id, StringComparison.Ordinal))
                ?? SelectedProfile;
        }
        catch (Exception ex)
        {
            ReportError(Describe(ex, "添加配置失败"));
            DesktopLog.Write("添加配置失败", ex);
        }
    }

    /// <summary>
    /// 自动查找当前游戏的 EXE 路径：来源是米哈游启动器登记的安装路径、启动器安装目录、最近运行记录、正在运行的游戏。
    /// 候选里跳过已经被现有配置使用的路径，按国服优先排序：
    /// 只有一个就直接填入；有多个就把它们交给界面弹窗让用户选（<see cref="ApplyAutoFindChoice"/>）；一个都没有就提示。
    /// </summary>
    public async Task<AutoFindOutcome> AutoFindExecutableAsync()
    {
        if (SelectedProfile is not { } editing)
        {
            return new AutoFindOutcome([], "请先选择一个配置");
        }

        var game = editing.Game;
        IReadOnlyList<GameExecutableCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => GameExecutableFinder.Find(game, _finderEnvironment)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DesktopLog.Write("自动查找游戏路径失败", ex);
            return Report(new AutoFindOutcome([], $"自动查找失败：{ex.Message}"), found: false);
        }

        // “已经加到配置里”：所有配置已保存的路径、界面里各套配置当前的路径（含当前这套）都不再推荐。
        var used = _data.Config.Profiles.Select(profile => profile.ExecutablePath)
            .Concat(Profiles.Select(profile => profile.ExecutablePath))
            .Append(editing.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('/', '\\').TrimEnd('\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = candidates
            .Where(candidate => !used.Contains(candidate.Path.TrimEnd('\\')))
            .OrderBy(candidate => candidate.Priority)
            .ToList();

        if (choices.Count == 0)
        {
            return Report(
                new AutoFindOutcome([], candidates.Count == 0
                    ? $"没有找到 {GameNames.Display(game)}。请点“浏览…”手动选择 EXE"
                    : $"没有找到新的 {GameNames.Display(game)}：找到的 {candidates.Count} 个安装都已经添加到配置里了"),
                found: false);
        }

        if (choices.Count == 1)
        {
            return ApplyAutoFindChoice(choices[0]);
        }

        return new AutoFindOutcome(choices, $"找到 {choices.Count} 个安装，请选择一个");
    }

    /// <summary>把用户在弹窗里选的（或唯一的）候选填入当前配置并保存。</summary>
    public AutoFindOutcome ApplyAutoFindChoice(GameExecutableCandidate choice)
    {
        if (SelectedProfile is not { } editing)
        {
            return new AutoFindOutcome([], "请先选择一个配置");
        }

        editing.ExecutablePath = choice.Path;
        return Report(new AutoFindOutcome([], $"已填入：{choice.Path}（{choice.Source}）"), found: true);
    }

    private AutoFindOutcome Report(AutoFindOutcome outcome, bool found)
    {
        if (found)
        {
            ReportSuccess(outcome.Message, TimeSpan.FromSeconds(5));
        }
        else
        {
            ReportInfo(outcome.Message);
        }

        return outcome;
    }

    /// <summary>复制当前配置（路径与全部设置）为一套新配置并立即保存，名称加“副本”。</summary>
    private void CopyProfile()
    {
        if (SelectedProfile is not { } source)
        {
            return;
        }

        Flush();
        try
        {
            var newId = ProfileEditorViewModel.New(_data.Config.Profiles.Select(profile => profile.Id), _scope).Id;
            var mutation = source.ToMutation() with { Id = newId, DisplayName = $"{source.DisplayName} 副本" };
            var saved = _data.AddProfile(mutation);
            StatusMessage = string.Empty;
            _selectedProfile = null;
            Refresh();
            SelectedProfile = Profiles.FirstOrDefault(item => string.Equals(item.Id, saved.Id, StringComparison.Ordinal))
                ?? SelectedProfile;
        }
        catch (Exception ex)
        {
            ReportError(Describe(ex, "复制配置失败"));
            DesktopLog.Write("复制配置失败", ex);
        }
    }

    private ProfileEditorViewModel Track(ProfileEditorViewModel editor)
    {
        editor.PropertyChanged += OnEditorChanged;
        return editor;
    }

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ProfileEditorViewModel editor
            || e.PropertyName is nameof(ProfileEditorViewModel.IsHoyoGame) or nameof(ProfileEditorViewModel.IsGenshin)
                or nameof(ProfileEditorViewModel.PathHint) or nameof(ProfileEditorViewModel.HasPathHint)
                or nameof(ProfileEditorViewModel.CommandLinePreview) or nameof(ProfileEditorViewModel.ResolutionControlsEnabled)
                or nameof(ProfileEditorViewModel.PresetEnabled) or nameof(ProfileEditorViewModel.CustomEnabled))
        {
            return;
        }

        if (e.PropertyName == nameof(ProfileEditorViewModel.ExecutablePath))
        {
            RenameByDetectedServer(editor);
        }

        SaveEditor(editor);
    }

    /// <summary>路径变了且名称还是自动生成的：按识别到的区服改名；用户自己起的名字不动，认不出区服也不动。</summary>
    private void RenameByDetectedServer(ProfileEditorViewModel editor)
    {
        if (!ProfileNames.IsAuto(editor.DisplayName)
            || ServerLabelDetector.Detect(editor.Game, editor.ExecutablePath, _registry) is not { } label)
        {
            return;
        }

        var others = Profiles.Where(item => !ReferenceEquals(item, editor) && item.Game == editor.Game).Select(item => item.DisplayName);
        editor.DisplayName = ProfileNames.Unique(label, others);
    }

    /// <summary>
    /// 把一套配置的当前编辑写入 config.json。不再有延迟：文本框只在“编辑完成”（失焦 / 回车）时才改属性，
    /// 开关、复选框、下拉框、数值框的值提交本来就是离散的，所以每次属性变化就是一次提交，直接保存。
    /// </summary>
    private void SaveEditor(ProfileEditorViewModel editing)
    {
        try
        {
            _data.UpdateProfile(editing.Id, editing.ToMutation());
            ReportSuccess("修改已保存");
        }
        catch (Exception ex)
        {
            ReportError(Describe(ex, "自动保存失败"));
            DesktopLog.Write($"自动保存配置失败（id={editing.Id}）", ex);
        }
    }

    /// <summary>保留给调用方（切游戏、返回主页、关闭窗口前）；编辑已经是立即保存，这里没有待写入的内容。</summary>
    public void Flush()
    {
    }

    /// <summary>名称只在有多套配置时展示，用来区分各套配置。</summary>
    public bool ShowProfileName => HasMultiple;

    private void Delete()
    {
        if (SelectedProfile is not { } editing)
        {
            return;
        }

        try
        {
            _data.RemoveProfile(editing.Id);
            _selectedProfile = null;
            StatusMessage = string.Empty;
            Refresh();
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
    private bool _overrideResolution;
    private bool _fullscreen = true;
    private bool _borderless;
    private bool _customResolution;
    private int _resolutionIndex;
    private double _customWidth = 1920;
    private double _customHeight = 1080;
    private string _extraArguments = string.Empty;

    /// <summary>仅用于 <see cref="New"/> 生成的待创建编辑项：转 mutation 时带上 ID。</summary>
    private bool IsNew { get; init; }

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
            OnPropertyChanged(nameof(PathHint));
            OnPropertyChanged(nameof(HasPathHint));
            OnPropertyChanged(nameof(IsGenshin));
        }
    }
    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (SetProperty(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(PathHint));
                OnPropertyChanged(nameof(HasPathHint));
            }
        }
    }

    /// <summary>路径即时检查结果（未设置 / 非完整路径 / 文件名不属于该游戏 / 文件不存在），合法时为空。</summary>
    public string PathHint => ExecutablePathCheck.Describe(Game, ExecutablePathCheck.Check(Game, ExecutablePath));

    public bool HasPathHint => PathHint.Length > 0;
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
    // ---- 启动设置（对齐 Starward 的“分辨率”区）：
    // 全屏 / 自定义分辨率 / 分辨率下拉 / 宽 × 高，启动前写入游戏注册表；“使用无边框窗口”自动加 -popupwindow；另有附加参数。
    // 与 Starward 不同的一点：多一个总开关，关闭时完全不碰游戏自己的分辨率与全屏设置。

    private static readonly (int Width, int Height)[] CommonResolutions =
    [
        (3840, 2160), (2560, 1600), (2560, 1440), (2048, 1536), (1920, 1440), (1920, 1200), (1920, 1080),
        (1680, 1050), (1600, 1200), (1440, 900), (1280, 960), (1280, 800), (1280, 720), (1152, 864),
        (1024, 768), (800, 600), (640, 480),
    ];

    /// <summary>分辨率下拉的项：常用分辨率中不超过主显示器的，加上主显示器本身（同 Starward），从大到小。</summary>
    public static (int Width, int Height)[] ResolutionList { get; } = BuildResolutionList(GetPrimaryScreenSize());

    public static string[] ResolutionOptions { get; } = ResolutionList.Select(item => $"{item.Width} × {item.Height}").ToArray();

    public static (int Width, int Height)[] BuildResolutionList((int Width, int Height) screen)
    {
        var list = CommonResolutions.Where(item => item.Width <= screen.Width && item.Height <= screen.Height).ToList();
        if (screen.Width > 0 && screen.Height > 0 && !list.Contains(screen))
        {
            list.Add(screen);
        }

        if (list.Count == 0)
        {
            list.Add(CommonResolutions[^1]);
        }

        return list.OrderByDescending(item => item.Width).ThenByDescending(item => item.Height).ToArray();
    }

    private static (int Width, int Height) GetPrimaryScreenSize() => (GetSystemMetrics(0), GetSystemMetrics(1));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>总开关：开启才会在启动前写入分辨率与全屏设置；关闭时不修改游戏自己的设置。</summary>
    public bool OverrideResolution
    {
        get => _overrideResolution;
        set
        {
            if (SetProperty(ref _overrideResolution, value))
            {
                OnPropertyChanged(nameof(ResolutionControlsEnabled));
                OnPropertyChanged(nameof(PresetEnabled));
                OnPropertyChanged(nameof(CustomEnabled));
            }
        }
    }

    public bool Fullscreen { get => _fullscreen; set => SetProperty(ref _fullscreen, value); }

    /// <summary>使用无边框窗口：自动注入 -popupwindow，与总开关、是否全屏都无关。</summary>
    public bool Borderless
    {
        get => _borderless;
        set
        {
            if (SetProperty(ref _borderless, value))
            {
                OnPropertyChanged(nameof(CommandLinePreview));
            }
        }
    }

    public bool CustomResolution
    {
        get => _customResolution;
        set
        {
            if (SetProperty(ref _customResolution, value))
            {
                OnPropertyChanged(nameof(PresetEnabled));
                OnPropertyChanged(nameof(CustomEnabled));
            }
        }
    }

    public int ResolutionIndex
    {
        get => _resolutionIndex;
        set
        {
            // ComboBox 重建时会回写 -1，忽略。
            if (value >= 0 && value < ResolutionOptions.Length)
            {
                SetProperty(ref _resolutionIndex, value);
            }
        }
    }

    public double CustomWidth { get => _customWidth; set => SetProperty(ref _customWidth, value); }

    public double CustomHeight { get => _customHeight; set => SetProperty(ref _customHeight, value); }

    public bool ResolutionControlsEnabled => _overrideResolution;

    public bool PresetEnabled => _overrideResolution && !_customResolution;

    public bool CustomEnabled => _overrideResolution && _customResolution;

    public string ExtraArguments
    {
        get => _extraArguments;
        set
        {
            if (SetProperty(ref _extraArguments, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CommandLinePreview));
            }
        }
    }

    /// <summary>最终传给游戏的命令行参数（自动注入的无边框 + 附加参数），便于排错。</summary>
    public string CommandLinePreview
    {
        get
        {
            var text = LaunchArguments.Build(_borderless, _extraArguments);
            return text.Length == 0 ? "没有附加的命令行参数" : text;
        }
    }

    private LaunchSettings BuildLaunch()
    {
        int? width = null, height = null;
        bool? fullscreen = null;
        if (_overrideResolution)
        {
            fullscreen = _fullscreen;
            if (_customResolution)
            {
                if (!double.IsNaN(_customWidth) && !double.IsNaN(_customHeight))
                {
                    width = Convert.ToInt32(_customWidth);
                    height = Convert.ToInt32(_customHeight);
                }
            }
            else if (_resolutionIndex >= 0 && _resolutionIndex < ResolutionList.Length)
            {
                (width, height) = ResolutionList[_resolutionIndex];
            }
        }

        return new LaunchSettings
        {
            Width = width,
            Height = height,
            Fullscreen = fullscreen,
            Borderless = _borderless,
            ExtraArguments = string.IsNullOrWhiteSpace(_extraArguments) ? null : _extraArguments.Trim(),
        };
    }

    private void ApplyLaunch(LaunchSettings? launch)
    {
        _borderless = launch?.Borderless ?? false;
        _extraArguments = launch?.ExtraArguments ?? string.Empty;
        _overrideResolution = launch is { Fullscreen: not null } || launch is { Width: not null };
        _fullscreen = launch?.Fullscreen ?? true;
        if (launch?.Width is { } width && launch.Height is { } height)
        {
            var preset = Array.FindIndex(ResolutionList, item => item.Width == width && item.Height == height);
            _customResolution = preset < 0;
            _resolutionIndex = preset >= 0 ? preset : 0;
            _customWidth = width;
            _customHeight = height;
        }
        else
        {
            _customResolution = false;
            _resolutionIndex = Math.Max(0, Array.FindIndex(ResolutionList, item => item.Width == 1920 && item.Height == 1080));
        }
    }

    public bool IsHoyoGame => Game != GameType.ZenlessZoneZero;
    public bool IsGenshin => Game == GameType.Genshin;

    /// <summary>新建编辑项。ID 与名称预填可用默认值，立即保存即可用。</summary>
    public static ProfileEditorViewModel New(IEnumerable<string> existingIds, GameType game, IEnumerable<string>? existingNames = null)
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
            // 中性的“配置 N”；填入路径后按识别到的区服改名。
            DisplayName = ProfileNames.NextDefault(existingNames ?? []),
        };
    }

    public static ProfileEditorViewModel From(GameProfile profile) => new ProfileEditorViewModel
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
    }.WithLaunch(profile.Launch);

    private ProfileEditorViewModel WithLaunch(LaunchSettings? launch)
    {
        ApplyLaunch(launch);
        return this;
    }

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
        GenshinTouchUiScalePercent: IsGenshin ? ToFps(GenshinTouchUiScalePercent, 400) : null,
        Launch: BuildLaunch());

    /// <summary>NumberBox 被清空时值为 NaN，直接 Convert.ToInt32 会抛 OverflowException。</summary>
    private static int ToFps(double value, int fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : Convert.ToInt32(value);
}

/// <summary>自动查找的结果：<see cref="Choices"/> 非空表示有多个候选等用户选择；否则 <see cref="Message"/> 就是给用户看的结果。</summary>
public sealed record AutoFindOutcome(IReadOnlyList<GameExecutableCandidate> Choices, string Message);
