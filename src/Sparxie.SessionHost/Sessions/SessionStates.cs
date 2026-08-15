namespace Sparxie.SessionHost.Sessions;

/// <summary>会话状态字符串，与 RPC 事件契约一致。</summary>
public static class SessionStates
{
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string Exited = "Exited";
    public const string Failed = "Failed";
}
