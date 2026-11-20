using System.Diagnostics;
using Sparxie.Contracts.Models;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Configuration;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Sparxie.SessionHost.Sessions;

/// <summary>
/// 第三方游戏模块的控制器：经 C ABI 函数表调用插件，所有调用都在会话专用线程上、带超时。
/// 插件在 SessionHost 进程内加载（与内置 Runtime 同一信任边界）：插件崩溃会连带 SessionHost 退出，
/// 注册表恢复靠下次启动时的遗留备份。
/// </summary>
public sealed class NativeGameController(PluginEntry entry) : IGameController
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    private NativePluginLibrary? _library;
    private NativePluginSession? _session;
    private RegistryHookSession? _registryHooks;
    private bool _aborted;
    private bool _disposed;

    public bool CreatesProcess => entry.Manifest!.HasCap("createsProcess");

    public async Task PrepareLaunchAsync(ProfileSnapshot profile, CancellationToken cancellationToken)
    {
        _library = NativePluginLibrary.Load(entry);
        _session = new NativePluginSession(_library);

        var arguments = entry.Manifest!.HasCap("needsArgs") ? LaunchArguments.Build(profile.Launch.ToModel()) : string.Empty;
        var dataDir = Path.Combine(AppDataDirectory.Resolve(), "plugin-data", entry.Id);
        Directory.CreateDirectory(dataDir);
        var logDir = Path.Combine(AppDataDirectory.Resolve(), "logs");
        Directory.CreateDirectory(logDir);

        // settings_json：schema 校验与表单尚未接入，先传空对象。
        _registryHooks = CreateRegistryHooks();
        await _session.CreateAsync(profile.ExecutablePath, arguments, "{}", logDir, dataDir, CallTimeout, _registryHooks).ConfigureAwait(false);
        await _session.PrepareLaunchAsync(CallTimeout).ConfigureAwait(false);
    }

    public async Task InstallAsync(Process? gameProcess, IntPtr jobHandle, HoyoProfileSettings? hoyo, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("插件会话未创建");

        // 只有模块自己创建进程时才由模块绑定 Job；其余进程已由 SessionHost 绑定。
        if (CreatesProcess && jobHandle != IntPtr.Zero)
        {
            await session.AttachJobAsync(jobHandle, CallTimeout).ConfigureAwait(false);
        }

        await session.InstallAsync(gameProcess is null ? 0u : (uint)gameProcess.Id, InstallTimeout).ConfigureAwait(false);
    }

    public Task PostInstallAsync(CancellationToken cancellationToken) =>
        (_session ?? throw new InvalidOperationException("插件会话未创建")).PostInstallAsync(CallTimeout);

    public async Task AbortAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _aborted)
        {
            return;
        }

        _aborted = true;
        try
        {
            await _session.AbortAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PluginCallException or TimeoutException)
        {
            // 失败路径清理：插件自己的 abort 失败不再向外抛，避免掩盖原始错误。
        }
    }

    public async Task WaitExitAsync(CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("插件会话未创建");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested(); // 取消不能当成"游戏已退出"返回，否则会误报 Exited
            if (await session.WaitExitAsync(500, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    public Task SetParamAsync(string key, long value, CancellationToken cancellationToken)
    {
        if (!entry.Manifest!.HasCap("hotParams"))
        {
            throw new NotSupportedException("该模块不支持热调");
        }

        return (_session ?? throw new InvalidOperationException("插件会话未创建")).SetParamAsync(key, value, CallTimeout);
    }

    /// <summary>
    /// manifest 声明了 registryHooks 时：先处理上次异常退出遗留的备份（调用方已确认游戏没在运行），再创建本次会话的钩子。
    /// 与画面设置的注册表逻辑完全独立。
    /// </summary>
    private RegistryHookSession? CreateRegistryHooks()
    {
        if (entry.Manifest!.RegistryHooks is not { } spec)
        {
            return null;
        }

        var store = new CurrentUserRegistryHookStore();
        var backupPath = RegistryHookSession.BackupPathFor(AppDataDirectory.Resolve(), entry.Id);
        if (!RegistryHookSession.RestoreLeftover(backupPath, store))
        {
            throw new InvalidOperationException($"插件 {entry.Id} 上次遗留的注册表备份无法恢复，已拒绝启动（备份保留在 {backupPath}）");
        }

        return new RegistryHookSession(entry.Id, spec.Keys, store, backupPath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose(); // 先 release 会话（插件不再改键），再恢复注册表
        _session = null;
        _registryHooks?.Restore();
        _library?.Dispose();
    }
}
