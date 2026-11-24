using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 同一程序目录只允许一个桌面实例：重复启动时通知已有实例显示并激活窗口，自己随即退出。
/// 以数据根目录（即程序目录）区分，所以不同路径的副本互不影响。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent, Action onActivate)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _wait = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// 尝试成为主实例。成功返回对象（需持有到进程结束）；已有实例时通知它并返回 null。
    /// <paramref name="onActivate"/> 在线程池线程上回调，需自行切回 UI 线程。
    /// </summary>
    public static SingleInstance? TryAcquire(string dataRoot, Action onActivate)
    {
        var name = NameFor(dataRoot);
        var mutex = new Mutex(true, $@"Local\Sparxie.Instance.{name}", out var created);
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Sparxie.Activate.{name}");
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

    /// <summary>路径不区分大小写与末尾分隔符，取哈希避免互斥体名里出现非法字符。</summary>
    internal static string NameFor(string dataRoot)
    {
        var normalized = Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _activateEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
