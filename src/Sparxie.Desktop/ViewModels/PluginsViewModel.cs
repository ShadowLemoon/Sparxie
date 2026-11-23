using System.Collections.ObjectModel;
using System.Text;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Desktop.ViewModels;

/// <summary>需要用户确认的操作（标题 + 正文）；由界面层用对话框显示。</summary>
public sealed record PluginConfirmation(string Title, string Body, string PrimaryText, PluginEnableDetails? Enable = null);

/// <summary>启用确认框用的结构化信息（界面层据此排版：风险句、能力、等宽分组哈希、变化时的新旧对比、必选勾选框）。</summary>
public sealed record PluginEnableDetails(
    string Name,
    string Id,
    string? Version,
    string? License,
    string? Homepage,
    IReadOnlyList<string> ExeNames,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> RiskSentences,
    string DllSha256,
    string ManifestSha256,
    long DllSize,
    string Directory,
    string? PreviousDllSha256,
    string? PreviousManifestSha256,
    bool IsChange,
    bool RequiresInjectionAck);

/// <summary>设置页里的一个第三方插件。</summary>
public sealed class PluginItemViewModel
{
    public PluginItemViewModel(PluginEntry entry, PluginsViewModel owner)
    {
        Entry = entry;
        Title = entry.Manifest?.DisplayName is { Length: > 0 } name ? $"{name}（{entry.Id}）" : entry.Id;
        Status = PluginsViewModel.StatusText(entry);
        Detail = entry.Reason ?? (entry.Manifest is null ? string.Empty : string.Join("、", PluginsViewModel.CapabilityLabels(entry.Manifest)));
        CanEnable = entry.Availability is PluginAvailability.Untrusted or PluginAvailability.HashChanged;
        CanDisable = entry.Availability == PluginAvailability.Ready;
        EnableCommand = new AsyncRelayCommand(() => owner.EnableAsync(entry.Id));
        DisableCommand = new RelayCommand(() => owner.Disable(entry.Id));
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(entry.Id));
    }

    public PluginEntry Entry { get; }

    public string Title { get; }

    public string Status { get; }

    public string Detail { get; }

    public bool CanEnable { get; }

    public bool CanDisable { get; }

    public string EnableLabel => Entry.Availability == PluginAvailability.HashChanged ? "重新确认并启用" : "查看并启用";

    public AsyncRelayCommand EnableCommand { get; }

    public RelayCommand DisableCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }
}

/// <summary>
/// 第三方游戏插件管理：列出 plugins\ 下的插件，启用前显示哈希与风险并要求用户确认，停用/删除时清理信任记录。
/// 这里只读 plugin.json 和 DLL 哈希，不会加载或执行任何插件代码。
/// </summary>
public sealed class PluginsViewModel : ObservableObject
{
    private readonly PluginCatalog _catalog;
    private readonly PluginTrustStore _trust;
    private readonly string _dataRoot;
    private readonly Action<string>? _onRemoved;
    private readonly IRegistryHookStore _registry;
    private string _message = string.Empty;
    private bool _ready;

    public PluginsViewModel(PluginCatalog catalog, PluginTrustStore trust, string dataRoot, Action<string>? onRemoved = null, IRegistryHookStore? registry = null)
    {
        _catalog = catalog;
        _trust = trust;
        _dataRoot = dataRoot;
        _onRemoved = onRemoved;
        _registry = registry ?? new CurrentUserRegistryHookStore();
        Confirm = _ => Task.FromResult(false); // 界面层接管前一律视为拒绝（默认不启用）
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
        _ready = true;
    }

    /// <summary>插件列表被重新读取之后触发（启用/停用/删除/手动刷新）。让主页等依赖"可用游戏列表"的地方刷新。</summary>
    public event Action? Changed;

    public ObservableCollection<PluginItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public RelayCommand RefreshCommand { get; }

    /// <summary>由界面层设置：显示确认对话框，返回用户是否同意。</summary>
    public Func<PluginConfirmation, Task<bool>> Confirm { get; set; }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message.Length > 0;

    public void Refresh()
    {
        Items.Clear();
        foreach (var entry in _catalog.Scan())
        {
            Items.Add(new PluginItemViewModel(entry, this));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));

        if (_ready)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>导入压缩包或文件夹。只拷贝和校验，不启用：之后仍要用户在列表里"查看并启用"。</summary>
    public async Task ImportAsync(string source)
    {
        var result = await Task.Run(() => PluginInstaller.Install(source, _catalog.PluginsRoot, Path.Combine(_dataRoot, "plugin-staging"))).ConfigureAwait(true);
        Message = result.Success
            ? $"{result.Message}：{result.Id}。{(result.Replaced ? "文件已变化，需要重新确认后才能使用。" : "请在下面的列表里查看哈希并确认后启用。")}"
            : result.Message;
        Refresh();
    }

    public async Task EnableAsync(string id)
    {
        var entry = _catalog.Scan().FirstOrDefault(item => item.Id == id);
        if (entry is null || entry.Manifest is null || entry.Availability is not (PluginAvailability.Untrusted or PluginAvailability.HashChanged))
        {
            Message = "该插件当前无法启用，请刷新后重试。";
            Refresh();
            return;
        }

        var details = BuildEnableDetails(entry, _trust.Find(id));
        var title = details.IsChange ? "插件文件已变化，重新确认？" : "启用第三方插件？";
        var confirmed = await Confirm(new PluginConfirmation(title, BuildRiskText(entry), "我已了解风险，启用", details)).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        // 确认期间文件可能被替换：以用户看到的哈希为准，不一致就取消。
        var latest = _catalog.Scan().FirstOrDefault(item => item.Id == id);
        if (latest is null || latest.DllSha256 != entry.DllSha256 || latest.ManifestSha256 != entry.ManifestSha256 || latest.DllSize != entry.DllSize)
        {
            Message = "插件文件在确认期间发生了变化，已取消启用。";
            Refresh();
            return;
        }

        try
        {
            _trust.Confirm(new PluginTrustRecord(id, latest.DllSha256!, latest.ManifestSha256!, latest.DllSize, DateTimeOffset.UtcNow));
            Message = $"已启用 {entry.Manifest.DisplayName}。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"保存信任记录失败：{ex.Message}";
        }

        Refresh();
    }

    /// <summary>停用：只撤销信任，文件保留；之后需要重新确认才能启用。</summary>
    /// <summary>
    /// 停用/删除之前处理该插件遗留的注册表钩子备份（SessionHost 异常退出后可能还留着）。
    /// 游戏正在运行时不动注册表（会和运行中的游戏冲突）。返回 null 表示没问题，否则是给用户看的原因。
    /// </summary>
    private string? RestoreLeftoverBackup(PluginEntry? entry, string id)
    {
        if (entry?.Manifest is { } manifest && GameProcessDetector.IsAnyRunning(manifest.ExeNames))
        {
            return "该插件对应的游戏正在运行，请先退出游戏";
        }

        var backup = RegistryHookSession.BackupPathFor(_dataRoot, id);
        return RegistryHookSession.RestoreLeftover(backup, _registry)
            ? null
            : $"该插件遗留的注册表备份无法恢复，已保留在 {backup}";
    }

    public void Disable(string id)
    {
        // 停用后插件不再 Ready，遗留备份就没有机会在启动前被恢复，所以先尝试恢复；做不到也不阻止停用，但要告诉用户。
        var restoreProblem = RestoreLeftoverBackup(_catalog.Scan().FirstOrDefault(item => item.Id == id), id);
        try
        {
            _trust.Remove(id);
            Message = restoreProblem is null
                ? "已停用，该游戏的配置会保留但无法启动。"
                : $"已停用，但{restoreProblem}；备份保留，重新启用该插件后下次启动前会再次尝试恢复。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"保存信任记录失败：{ex.Message}";
        }

        Refresh();
    }

    /// <summary>删除：撤销信任、删除 plugins\&lt;id&gt; 目录和遗留的恢复备份，并通知上层清理偏好里的记录。</summary>
    public async Task DeleteAsync(string id)
    {
        var entry = _catalog.Scan().FirstOrDefault(item => item.Id == id);
        if (entry is null)
        {
            Refresh();
            return;
        }

        var body = $"将删除插件目录及其全部文件：\n{entry.Directory}\n\n使用该游戏的配置会保留，但需要重新安装插件才能再次启动。此操作不可撤销。";
        if (!await Confirm(new PluginConfirmation("删除插件？", body, "删除")).ConfigureAwait(true))
        {
            return;
        }

        // 删除后就没有插件能再触发恢复了：遗留的注册表备份必须先恢复成功，否则拒绝删除（避免 HKCU 永久停在插件改写后的值）。
        if (RestoreLeftoverBackup(entry, id) is { } restoreProblem)
        {
            Message = $"未删除：{restoreProblem}。";
            Refresh();
            return;
        }

        try
        {
            _trust.Remove(id);
            Directory.Delete(entry.Directory, recursive: true);
            _onRemoved?.Invoke(id);
            Message = "已删除插件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"删除失败（文件可能正被占用）：{ex.Message}";
        }

        Refresh();
    }

    public static string StatusText(PluginEntry entry) => entry.Availability switch
    {
        PluginAvailability.Ready => "已启用",
        PluginAvailability.Untrusted => "未启用（需要确认）",
        PluginAvailability.HashChanged => "文件已变化，需要重新确认",
        PluginAvailability.AbiTooNew => "需要更新版本的 Sparxie",
        _ => "无效",
    };

    public static IEnumerable<string> CapabilityLabels(PluginManifest manifest)
    {
        if (manifest.HasCap("injects")) { yield return "会注入游戏进程"; }
        if (manifest.HasCap("createsProcess")) { yield return "自行创建游戏进程"; }
        if (manifest.HasCap("needsArgs")) { yield return "需要启动参数"; }
        if (manifest.HasCap("hotParams")) { yield return "支持热调"; }
        if (manifest.RegistryHooks is { Keys.Count: > 0 }) { yield return "会修改注册表（仅限声明的 HKCU 项）"; }
    }

    /// <summary>每 8 位一组、空格分隔，便于逐段核对（不改变哈希本身：复制时用原值）。</summary>
    public static string FormatHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(hash.Length + hash.Length / 8);
        for (var i = 0; i < hash.Length; i += 8)
        {
            if (i > 0) { builder.Append(' '); }
            builder.Append(hash, i, Math.Min(8, hash.Length - i));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 启用确认框的结构化内容。<paramref name="previous"/> 是该插件上次被确认时记录的哈希：
    /// 有记录且与当前不同就是“文件已变化”，界面要把新旧哈希并排给用户看。
    /// </summary>
    public static PluginEnableDetails BuildEnableDetails(PluginEntry entry, PluginTrustRecord? previous)
    {
        var manifest = entry.Manifest!;
        var injects = manifest.HasCap("injects");
        var risks = new List<string>
        {
            "它是第三方插件，未经 Sparxie 审计，没有沙箱。",
            "启用后它会以管理员权限运行原生代码，可能读取或修改你的文件和游戏进程。",
        };
        if (injects)
        {
            risks.Add("它声明会向游戏进程注入代码：可能违反游戏的用户协议，并有被反作弊系统处理（包括封号）的风险。");
        }

        if (manifest.HasCap("createsProcess"))
        {
            risks.Add("它会自己创建游戏进程。");
        }

        if (manifest.RegistryHooks is { Keys.Count: > 0 } hooks)
        {
            risks.Add($"它会修改 {hooks.Keys.Count} 个声明的当前用户注册表项，Sparxie 会在改动前备份、结束后恢复。");
        }

        var isChange = previous is not null && (previous.DllSha256 != entry.DllSha256 || previous.ManifestSha256 != entry.ManifestSha256);
        return new PluginEnableDetails(
            manifest.DisplayName,
            entry.Id,
            manifest.Version,
            manifest.License,
            manifest.Homepage,
            manifest.ExeNames,
            CapabilityLabels(manifest).ToList(),
            risks,
            entry.DllSha256 ?? string.Empty,
            entry.ManifestSha256 ?? string.Empty,
            entry.DllSize,
            entry.Directory,
            isChange ? previous!.DllSha256 : null,
            isChange ? previous!.ManifestSha256 : null,
            isChange,
            injects);
    }

    /// <summary>启用前给用户看的完整信息：来源、哈希、能力与风险。</summary>
    public static string BuildRiskText(PluginEntry entry)
    {
        var manifest = entry.Manifest!;
        var text = new StringBuilder();
        text.AppendLine("这是第三方插件，未经 Sparxie 审计。启用后它会以管理员权限运行原生代码，可能读取或修改你的文件和游戏进程。");
        text.AppendLine("只启用你信任其来源的插件；Sparxie 无法验证它的行为。");
        if (manifest.HasCap("injects"))
        {
            text.AppendLine();
            text.AppendLine("该插件声明会向游戏进程注入代码：可能违反游戏的用户协议，并有被反作弊系统处理（包括封号）的风险。");
        }

        if (manifest.HasCap("createsProcess"))
        {
            text.AppendLine("该插件会自己创建游戏进程。");
        }

        text.AppendLine();
        text.AppendLine($"名称：{manifest.DisplayName}");
        text.AppendLine($"标识：{entry.Id}");
        if (manifest.Version is { Length: > 0 }) { text.AppendLine($"版本：{manifest.Version}"); }
        if (manifest.License is { Length: > 0 }) { text.AppendLine($"许可：{manifest.License}"); }
        if (manifest.Homepage is { Length: > 0 }) { text.AppendLine($"主页：{manifest.Homepage}"); }
        text.AppendLine($"可启动的程序：{string.Join("、", manifest.ExeNames)}");
        text.AppendLine($"位置：{entry.Directory}");
        text.AppendLine($"DLL 大小：{entry.DllSize:N0} 字节");
        text.AppendLine($"DLL SHA-256：{entry.DllSha256}");
        text.Append($"plugin.json SHA-256：{entry.ManifestSha256}");
        return text.ToString();
    }
}
