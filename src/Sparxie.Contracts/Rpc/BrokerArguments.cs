namespace Sparxie.Contracts.Rpc;

/// <summary>Broker 提权启动参数的受控契约。生产入口只允许传递随机管道名。</summary>
public static class BrokerProcessArguments
{
    public const string PipeNameOption = "--pipe-name=";
    public const int MaxPipeNameLength = 128;

    public static string Build(string pipeName)
    {
        if (!IsValidPipeName(pipeName))
        {
            throw new ArgumentException("Broker 管道名非法", nameof(pipeName));
        }

        return PipeNameOption + pipeName;
    }

    public static bool TryParse(
        IReadOnlyList<string> args,
        out string? pipeName,
        out string error)
    {
        pipeName = null;
        if (args.Count != 1)
        {
            error = "Broker 只接受一个 --pipe-name 参数";
            return false;
        }

        if (!args[0].StartsWith(PipeNameOption, StringComparison.Ordinal))
        {
            error = "Broker 只接受 --pipe-name=<name> 参数";
            return false;
        }

        var value = args[0][PipeNameOption.Length..];
        if (!IsValidPipeName(value))
        {
            error = "Broker 管道名包含非法字符或长度超限";
            return false;
        }

        pipeName = value;
        error = string.Empty;
        return true;
    }

    public static bool IsValidPipeName(string? pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > MaxPipeNameLength)
        {
            return false;
        }

        foreach (var ch in pipeName)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
