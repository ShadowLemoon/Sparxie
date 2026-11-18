using Sparxie.Contracts.Models;
using HostOptions = Sparxie.SessionHost.Hosting.HostOptions;

namespace Sparxie.SessionHost.Sessions;

/// <summary>游戏模块：按游戏 id 创建控制器。只在 SessionHost 内实例化。</summary>
public interface IGameModule
{
    string Id { get; }

    IGameController CreateController(HostOptions options);
}

/// <summary>模块表：游戏 id → 模块。新增游戏只需登记一项，不再修改 HostService 里的判断。</summary>
public sealed class GameModuleTable
{
    public static GameModuleTable BuiltIn { get; } = new(
    [
        new DelegateModule(GameIds.Genshin, _ => new HoyoGameController()),
        new DelegateModule(GameIds.StarRail, _ => new HoyoGameController()),
        // SessionHost 已禁用 CET（上游 asm_syscall 不兼容 shadow stack），Hoyo DLL 加载不再崩溃。
        new DelegateModule(GameIds.ZenlessZoneZero, options => new ZzzGameController(options.SessionId)),
    ]);

    private readonly Dictionary<string, IGameModule> _modules;

    public GameModuleTable(IEnumerable<IGameModule> modules) =>
        _modules = modules.ToDictionary(module => module.Id, StringComparer.Ordinal);

    public IGameModule? Find(string? id) => id is not null && _modules.TryGetValue(id, out var module) ? module : null;

    /// <summary>未登记的 id 抛 InvalidOperationException（Broker 已先行拒绝，这里是兜底）。</summary>
    public IGameController CreateController(HostOptions options) =>
        (Find(options.Profile.Game) ?? throw new InvalidOperationException($"未知 game: {options.Profile.Game}"))
        .CreateController(options);

    private sealed class DelegateModule(string id, Func<HostOptions, IGameController> factory) : IGameModule
    {
        public string Id { get; } = id;

        public IGameController CreateController(HostOptions options) => factory(options);
    }
}
