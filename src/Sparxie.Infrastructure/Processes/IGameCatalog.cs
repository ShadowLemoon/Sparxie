using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>某款游戏的静态描述（只读元数据，不执行任何游戏代码）。</summary>
public sealed record GameDescriptor(string Id, string DisplayName, IReadOnlyList<string> ExeNames);

/// <summary>游戏目录：按游戏 id 查询静态描述。当前只含内置三款，后续由插件目录扩展。</summary>
public interface IGameCatalog
{
    IReadOnlyList<GameDescriptor> All { get; }

    GameDescriptor? Find(string? id);
}

/// <summary>内置游戏目录。EXE 白名单仍由 <see cref="GameExecutables"/> 的内置表维护，这里只做 id 级查询。</summary>
public sealed class BuiltInGameCatalog : IGameCatalog
{
    public static BuiltInGameCatalog Instance { get; } = new();

    private readonly GameDescriptor[] _all;

    private BuiltInGameCatalog()
    {
        _all = Enum.GetValues<GameType>()
            .Select(game => new GameDescriptor(GameIds.ToId(game), game.ToString(), GameExecutables.For(game)))
            .ToArray();
    }

    public IReadOnlyList<GameDescriptor> All => _all;

    public GameDescriptor? Find(string? id) =>
        GameIds.TryGetBuiltIn(id, out var game) ? _all.First(item => item.Id == GameIds.ToId(game)) : null;
}
