namespace Sparxie.LauncherCore;

public enum LauncherFailureKind
{
    InvalidArguments,
    Configuration,
    ProfileSelection,
    BrokerConnection,
    BrokerRejected,
    SessionFault,
}

/// <summary>启动器核心向宿主暴露的稳定失败分类，不包含 native 句柄或指针。</summary>
public class LauncherException : Exception
{
    public LauncherException(LauncherFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public LauncherException(LauncherFailureKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public LauncherFailureKind Kind { get; }
}

public sealed class LauncherSessionRejectedException : LauncherException
{
    public LauncherSessionRejectedException(string sessionMessage)
        : base(LauncherFailureKind.BrokerRejected, sessionMessage)
    {
    }
}
