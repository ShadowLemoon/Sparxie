using System.Text;
using System.Text.RegularExpressions;

namespace Sparxie.Infrastructure.Diagnostics;

/// <summary>
/// 脱敏诊断包：只包含日志与非敏感版本/契约信息。
/// 排除 config.json、config.invalid-*.json、recovery/、凭据与未脱敏完整用户路径。
/// </summary>
public static class DiagnosticExporter
{
    private static readonly string[] ExcludedRelativeSegments =
    {
        "config.json",
        "config.invalid-",
        "recovery",
        "diagnostics",
    };

    /// <summary>
    /// 导出诊断包到目标目录。源为应用根目录下的 logs/ 与当前进程基本信息。
    /// 返回导出的文件列表。
    /// </summary>
    public static IReadOnlyList<string> Export(string appRoot, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        var exported = new List<string>();

        // 1) 日志：只复制 *.log，且内容脱敏（用户路径 → <USER>、Token/Secret → <REDACTED>）
        var logsDir = Path.Combine(appRoot, "logs");
        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                var dest = Path.Combine(targetDir, "logs", Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var content = Redact(File.ReadAllText(file));
                File.WriteAllText(dest, content, new UTF8Encoding(false));
                exported.Add(dest);
            }
        }

        // 2) 非敏感版本/契约信息
        var info = new StringBuilder();
        info.AppendLine($"Sparxie Diagnostic Package");
        info.AppendLine($"GeneratedUtc: {DateTime.UtcNow:O}");
        info.AppendLine($"OS: {Environment.OSVersion}");
        info.AppendLine($"ProcessArch: {Environment.Is64BitProcess}");
        info.AppendLine($"AppDir: <APP_DIR>");
        File.WriteAllText(Path.Combine(targetDir, "environment.txt"), info.ToString(), new UTF8Encoding(false));
        exported.Add(Path.Combine(targetDir, "environment.txt"));

        return exported;
    }

    /// <summary>脱敏：完整用户路径、Token/Secret/凭据等替换为占位符。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 常见用户目录根（C:\Users\xxx）
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            text = text.Replace(userProfile, "<USER>", StringComparison.OrdinalIgnoreCase);
        }

        // 盘符级完整路径（D:\...）统一脱敏为 <PATH>
        text = Regex.Replace(text, @"[A-Za-z]:\\[^\s,;""'\\)\]]+", "<PATH>");

        // 凭据样式：先处理 Bearer 令牌，再处理键值对，避免 \S+ 吞掉相邻令牌。
        text = Regex.Replace(text, @"(?i)Bearer\s+[A-Za-z0-9._-]+", "Bearer <REDACTED>");
        text = Regex.Replace(text, @"(?i)(token|secret|password|authorization)\s*[:=]\s*[A-Za-z0-9._-]+", "$1=<REDACTED>");

        return text;
    }

    /// <summary>判断路径是否属于不应进入诊断包的敏感位置。</summary>
    public static bool IsExcluded(string fullPath)
    {
        var normalized = fullPath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var segment in ExcludedRelativeSegments)
        {
            if (normalized.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
