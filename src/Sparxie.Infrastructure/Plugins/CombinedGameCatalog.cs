using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.Infrastructure.Plugins;

/// <summary>
/// 内置三款 + 已确认（Ready）的插件游戏。每次查询都会重新读取磁盘上的 manifest 和信任记录，下架立即生效；
/// 内置 id 永远优先，插件不能覆盖内置游戏。
/// </summary>
public sealed class CombinedGameCatalog(PluginCatalog plugins) : IGameCatalog
{
    public static string PluginsDirectory => Path.Combine(AppDataDirectory.Resolve(), "plugins");

    public static string TrustFilePath => Path.Combine(AppDataDirectory.Resolve(), "plugin-trust.json");

    /// <summary>按默认数据目录创建（<c>&lt;DataRoot&gt;\plugins</c> 与 <c>&lt;DataRoot&gt;\plugin-trust.json</c>）。</summary>
    public static CombinedGameCatalog CreateDefault() =>
        new(new PluginCatalog(PluginsDirectory, new PluginTrustStore(TrustFilePath)));

    public PluginCatalog Plugins { get; } = plugins;

    public IReadOnlyList<GameDescriptor> All => [.. BuiltInGameCatalog.Instance.All, .. Plugins.All];

    public GameDescriptor? Find(string? id) => BuiltInGameCatalog.Instance.Find(id) ?? Plugins.Find(id);

    /// <summary>EXE 白名单：内置游戏沿用 <see cref="GameExecutables"/>，插件游戏取 manifest 里的 exeNames；找不到一律拒绝。</summary>
    public bool IsAllowed(string? gameId, string executablePath)
    {
        if (Sparxie.Contracts.Models.GameIds.TryGetBuiltIn(gameId, out _))
        {
            return GameExecutables.IsAllowed(gameId, executablePath);
        }

        var name = Path.GetFileName(executablePath);
        return Plugins.Find(gameId)?.ExeNames.Contains(name, StringComparer.OrdinalIgnoreCase) == true;
    }
}
