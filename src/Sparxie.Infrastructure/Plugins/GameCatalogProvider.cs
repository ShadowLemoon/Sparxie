using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Infrastructure.Plugins;

/// <summary>内置游戏 + 一份固定的插件游戏快照。快照由桌面端在启动或插件变化时刷新，避免每次校验都去扫磁盘、给 DLL 算哈希。</summary>
public sealed class SnapshotGameCatalog(IEnumerable<GameDescriptor> plugins) : IGameCatalog
{
    private readonly GameDescriptor[] _plugins = [.. plugins];

    public IReadOnlyList<GameDescriptor> All => [.. BuiltInGameCatalog.Instance.All, .. _plugins];

    public GameDescriptor? Find(string? id) =>
        BuiltInGameCatalog.Instance.Find(id) ?? _plugins.FirstOrDefault(item => item.Id == id);
}

/// <summary>
/// 进程内的"当前游戏目录"：默认只有内置三款。桌面端把它设置成带插件游戏的快照后，
/// Profile 校验、EXE 路径检查等纯函数式的代码就能认识插件游戏的 EXE 白名单，而不必各自去扫插件目录。
/// 真正启动会话时的权威校验仍在 Broker/SessionHost 里每次读磁盘，这里只影响界面侧的即时检查。
/// </summary>
public static class GameCatalogProvider
{
    private static volatile IGameCatalog _current = BuiltInGameCatalog.Instance;

    public static IGameCatalog Current
    {
        get => _current;
        set => _current = value ?? BuiltInGameCatalog.Instance;
    }

    /// <summary>EXE 白名单：内置游戏沿用 <see cref="GameExecutables"/>；其余取当前快照里的 manifest 白名单；未知一律为空。</summary>
    public static IReadOnlyList<string> ExeNamesFor(string? gameId) =>
        GameIds.TryGetBuiltIn(gameId, out _) ? GameExecutables.For(gameId) : _current.Find(gameId)?.ExeNames ?? [];

    public static bool IsAllowed(string? gameId, string executablePath) =>
        ExeNamesFor(gameId).Contains(Path.GetFileName(executablePath), StringComparer.OrdinalIgnoreCase);
}
