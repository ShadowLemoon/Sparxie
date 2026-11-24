using System.Runtime.InteropServices;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 桌面端单实例：重复启动时通知已有实例显示并激活窗口，自己随即退出。
/// 互斥体名固定、不含路径和版本（与 Starward 一致）：同一用户会话里只允许一个实例。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly RegisteredWaitHandle _wait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent, Action onActivate)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _wait = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// 尝试成为主实例。成功返回对象；已有实例时通知它并返回 null。
    /// <paramref name="onActivate"/> 在线程池线程上回调，需自行切回 UI 线程。
    /// </summary>
    public static SingleInstance? TryAcquire(Action onActivate)
    {
        var mutex = new Mutex(true, @"Local\Sparxie.Instance", out var created);
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sparxie.Activate");
        if (!created)
        {
            try
            {
                AllowSetForegroundWindow(-1); // ASFW_ANY：让已有实例有权把窗口提到前台
                activateEvent.Set();
            }
            finally
            {
                activateEvent.Dispose();
                mutex.Dispose();
            }

            return null;
        }

        return new SingleInstance(mutex, activateEvent, onActivate);
    }

    /// <summary>退出开始时就释放，避免收尾较慢期间新启动被误判为重复实例；必须在获取它的线程调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wait.Unregister(null);
        _activateEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
