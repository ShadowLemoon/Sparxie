using Sparxie.Contracts.Models;

namespace Sparxie.Infrastructure.Processes;

/// <summary>启动设置的取值规则：配置校验（AppConfigValidator）与 Broker 快照校验共用。</summary>
public static class LaunchSettingsRules
{
    public const int MinWidth = 640;
    public const int MinHeight = 480;
    public const int MaxResolution = 16384;
    public const int MaxExtraArgumentsLength = 512;

    /// <summary>返回第一条违规说明；合法返回 null。宽高必须同时给出或同时缺省。</summary>
    public static string? Validate(int? width, int? height, string? extraArguments)
    {
        if (width.HasValue != height.HasValue)
        {
            return "分辨率的宽和高必须同时设置";
        }

        if (width is { } w && (w < MinWidth || w > MaxResolution))
        {
            return $"宽度超出 {MinWidth}–{MaxResolution}";
        }

        if (height is { } h && (h < MinHeight || h > MaxResolution))
        {
            return $"高度超出 {MinHeight}–{MaxResolution}";
        }

        if (extraArguments is not null)
        {
            if (extraArguments.Length > MaxExtraArgumentsLength)
            {
                return $"附加启动参数不能超过 {MaxExtraArgumentsLength} 个字符";
            }

            if (extraArguments.Any(char.IsControl))
            {
                return "附加启动参数不能包含换行等控制字符";
            }
        }

        return null;
    }

    public static string? Validate(LaunchSettings? launch) =>
        launch is null ? null : Validate(launch.Width, launch.Height, launch.ExtraArguments);
}
