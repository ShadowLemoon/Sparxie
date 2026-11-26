namespace Sparxie.Desktop.ViewModels;

/// <summary>
/// 主页上给所有用户看的启动错误（不再只在调试模式显示）。三段式：发生了什么 / 可以怎么做 / 技术详情。
/// 先按消息关键词做通用映射；等 IPC 有了结构化的插件错误码（设计文档 P1-6）再换成按错误码映射。
/// </summary>
public sealed record LaunchErrorInfo(string Title, string Hint, string Detail)
{
    public static LaunchErrorInfo FromStartFailure(string profileName, string message) => Build($"无法启动「{profileName}」", message);

    public static LaunchErrorInfo FromSessionEnd(string state, string? message)
    {
        var title = state.StartsWith("HostCrashed", StringComparison.Ordinal) ? "游戏会话的宿主进程意外退出" : "启动没有成功";
        return Build(title, string.IsNullOrWhiteSpace(message) ? state : message!);
    }

    /// <summary>加载配置时做过修复、移出或恢复：告诉用户，免得配置少了却不知道原因。没有改动返回 null。</summary>
    public static LaunchErrorInfo? FromConfigChanges(IReadOnlyList<string> changes) => changes.Count == 0
        ? null
        : new LaunchErrorInfo("配置文件有问题，已自动处理", "原文件保留在程序目录里。请核对下面列出的改动。", string.Join("\n", changes));

    /// <summary>是否是需要给用户展示的失败终态（正常退出不算）。</summary>
    public static bool IsFailureState(string state) => state is "Failed" or "HostCrashedBeforeRunning" or "HostCrashedAfterRunning";

    private static LaunchErrorInfo Build(string title, string detail)
    {
        var hint = HintFor(detail);
        return new LaunchErrorInfo(title, hint, detail.Trim());
    }

    private static string HintFor(string message)
    {
        if (message.Contains("数字签名", StringComparison.Ordinal) || message.Contains("签名", StringComparison.Ordinal))
        {
            return "游戏文件的厂商签名校验没有通过，插件拒绝继续。请确认游戏是官方完整安装的；如果刚更新过游戏，请先用官方启动器修复文件。";
        }

        if (message.Contains("插件", StringComparison.Ordinal) && (message.Contains("超时", StringComparison.Ordinal) || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
        {
            return "插件没有在规定时间内响应。可以重试；仍然失败时请到设置里停用该插件，并把下面的诊断信息发给插件作者。";
        }

        if (message.Contains("插件", StringComparison.Ordinal))
        {
            return "插件执行出错。可以重试；仍然失败时请到设置里停用该插件，并把下面的诊断信息发给插件作者。";
        }

        if (message.Contains("注册表", StringComparison.Ordinal))
        {
            return "插件改过的注册表项还没有恢复。请先关闭游戏，再到设置的「第三方游戏插件」里处理。";
        }

        if (message.Contains("管理员", StringComparison.Ordinal) || message.Contains("拒绝访问", StringComparison.Ordinal) || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return "权限不足。请允许 Sparxie 以管理员权限运行，并检查游戏目录是否可写。";
        }

        if (message.Contains("找不到", StringComparison.Ordinal) || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "找不到需要的文件。请检查游戏路径是否仍然有效。";
        }

        return "可以重试一次。仍然失败时，请复制诊断信息反馈。";
    }

    /// <summary>复制给用户反馈用的诊断文本：只含这次错误本身，不含其它配置。</summary>
    public string ToDiagnosticText(string game, DateTimeOffset now) =>
        $"[Sparxie 启动错误] {now:yyyy-MM-dd HH:mm:ss zzz}\n游戏：{game}\n{Title}\n{Detail}";
}
