using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 界面里的"一个游戏"：用 id 标识，内置三款与第三方插件游戏一视同仁。
/// 内置游戏专属的东西（背景图、图标、Hoyo 设置段）通过 <see cref="BuiltIn"/> 判断，第三方游戏拿不到就走通用路径。
/// </summary>
public readonly record struct GameKey(string Id)
{
    public static implicit operator GameKey(GameType game) => new(GameIds.ToId(game));

    /// <summary>内置游戏的枚举形式；第三方游戏为 null。</summary>
    public GameType? BuiltIn => GameIds.TryGetBuiltIn(Id, out var game) ? game : null;

    public bool IsThirdParty => BuiltIn is null;

    public string Display => GameDirectory.DisplayName(Id);

    public override string ToString() => Id;
}

/// <summary>
/// 桌面端可见的游戏列表：内置三款 + 用户已确认（Ready）的插件游戏。列表是快照，只在启动和插件变化时重新读取磁盘，
/// 同时把快照交给 <see cref="GameCatalogProvider"/>，让 Profile 校验和路径检查认识插件游戏的 EXE 白名单。
/// </summary>
public static class GameDirectory
{
    private static readonly object Gate = new();
    private static IReadOnlyList<GameDescriptor> _plugins = [];

    /// <summary>读取插件游戏的来源；测试里可替换，默认读真实的数据目录。</summary>
    public static Func<IReadOnlyList<GameDescriptor>> PluginSource { get; set; } = () => CombinedGameCatalog.CreateDefault().Plugins.All;

    public static IReadOnlyList<GameKey> Games
    {
        get
        {
            lock (Gate)
            {
                return [.. Enum.GetValues<GameType>().Select(game => (GameKey)game), .. _plugins.Select(item => new GameKey(item.Id))];
            }
        }
    }

    /// <summary>重新读取插件游戏列表，并更新全局目录快照。读取失败按"没有插件游戏"处理。返回列表是否发生了变化。</summary>
    public static bool Reload()
    {
        IReadOnlyList<GameDescriptor> loaded;
        try
        {
            loaded = PluginSource();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loaded = [];
        }

        lock (Gate)
        {
            static string Signature(GameDescriptor item) => $"{item.Id}|{item.DisplayName}|{string.Join(",", item.ExeNames)}";
            var changed = !_plugins.Select(Signature).SequenceEqual(loaded.Select(Signature));
            _plugins = loaded;
            if (changed)
            {
                Options = [.. Enum.GetValues<GameType>().Select(game => (GameKey)game), .. loaded.Select(item => new GameKey(item.Id))];
            }

            GameCatalogProvider.Current = new SnapshotGameCatalog(loaded);
            return changed;
        }
    }

    /// <summary>编辑页游戏下拉框用的选项数组：只在插件列表变化时换新实例，保证 ComboBox 的 ItemsSource 稳定。</summary>
    public static GameKey[] Options { get; private set; } = [.. Enum.GetValues<GameType>().Select(game => (GameKey)game)];

    public static string DisplayName(string id)
    {
        if (GameIds.TryGetBuiltIn(id, out var game))
        {
            return game switch
            {
                GameType.Genshin => "原神",
                GameType.StarRail => "崩坏：星穹铁道",
                GameType.ZenlessZoneZero => "绝区零",
                _ => id,
            };
        }

        lock (Gate)
        {
            return _plugins.FirstOrDefault(item => item.Id == id)?.DisplayName ?? id;
        }
    }

    public static IReadOnlyList<string> ExeNames(string id) => GameCatalogProvider.ExeNamesFor(id);
}
