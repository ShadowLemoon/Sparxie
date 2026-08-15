using Google.Protobuf;

namespace Sparxie.Contracts.Rpc;

/// <summary>SessionHost 进程环境变量契约与私有管道命名规则。</summary>
public static class HostEnvironment
{
    public const string SessionId = "SPARXIE_SESSION_ID";
    public const string HostPipeName = "SPARXIE_HOST_PIPE_NAME";
    public const string ProfileJson = "SPARXIE_PROFILE_JSON";

    public const string PipePrefix = "sparxie-host-";

    public static string PipeNameFor(string sessionId) => PipePrefix + sessionId;
}

/// <summary>Profile 快照的 JSON 序列化（protobuf JSON 契约）。</summary>
public static class ProfileJson
{
    public static string Format(ProfileSnapshot profile) => JsonFormatter.Default.Format(profile);

    public static ProfileSnapshot Parse(string json) => JsonParser.Default.Parse<ProfileSnapshot>(json);
}
