using Google.Protobuf;
using Sparxie.Contracts.Rpc;

namespace Sparxie.SessionHost.Hosting;

/// <summary>Host 启动参数：全部经环境变量传入（管道名、会话 ID、Profile 快照 JSON）。</summary>
public sealed record HostOptions(string SessionId, string HostPipeName, ProfileSnapshot Profile)
{
    public static HostOptions FromEnvironment()
    {
        var sessionId = Environment.GetEnvironmentVariable(HostEnvironment.SessionId);
        var pipeName = Environment.GetEnvironmentVariable(HostEnvironment.HostPipeName);
        var profileJson = Environment.GetEnvironmentVariable(HostEnvironment.ProfileJson);

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException($"{HostEnvironment.SessionId} 未设置");
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new InvalidOperationException($"{HostEnvironment.HostPipeName} 未设置");
        }

        if (string.IsNullOrWhiteSpace(profileJson))
        {
            throw new InvalidOperationException($"{HostEnvironment.ProfileJson} 未设置");
        }

        ProfileSnapshot profile;
        try
        {
            profile = ProfileJson.Parse(profileJson);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidOperationException($"{HostEnvironment.ProfileJson} 无法解析", ex);
        }

        return new HostOptions(sessionId, pipeName, profile);
    }
}
