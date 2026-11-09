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

    public const string ParentPidOption = "--parent-pid=";

    /// <summary>管道名之后追加父进程 PID：Broker 据此随启动它的主程序一起退出（生命周期绑定）。</summary>
    public static string Build(string pipeName, int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }

        return Build(pipeName) + " " + ParentPidOption + parentProcessId;
    }

    /// <summary>解析 <c>--pipe-name=&lt;name&gt; [--parent-pid=&lt;pid&gt;]</c>；父 PID 可选。</summary>
    public static bool TryParse(
        IReadOnlyList<string> args,
        out string? pipeName,
        out int? parentProcessId,
        out string error)
    {
        parentProcessId = null;
        if (args.Count == 2)
        {
            if (!args[1].StartsWith(ParentPidOption, StringComparison.Ordinal)
                || !int.TryParse(args[1][ParentPidOption.Length..], out var pid)
                || pid <= 0)
            {
                pipeName = null;
                error = "Broker 只接受 --parent-pid=<正整数> 作为第二个参数";
                return false;
            }

            parentProcessId = pid;
            return TryParse([args[0]], out pipeName, out error);
        }

        return TryParse(args, out pipeName, out error);
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
