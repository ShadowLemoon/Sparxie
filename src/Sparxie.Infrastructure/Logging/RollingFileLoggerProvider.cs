using Microsoft.Extensions.Logging;

namespace Sparxie.Infrastructure.Logging;

/// <summary>
/// 滚动文件日志：写启动器程序目录 logs/{name}-yyyyMMdd.log，
/// 默认保留 7 天，超过保留期的日志自动清理（不影响活动日志）。
/// 供 Broker/SessionHost 通过 AddProvider 接入 ILogger。
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDir;
    private readonly string _name;
    private readonly int _retentionDays;

    public RollingFileLoggerProvider(string appRoot, string name, int retentionDays = 7)
    {
        _logsDir = Path.Combine(appRoot, "logs");
        _name = Sanitize(name);
        _retentionDays = Math.Max(1, retentionDays);
        Directory.CreateDirectory(_logsDir);
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal string ResolveTodayPath() => Path.Combine(_logsDir, $"{_name}-{DateTime.Now:yyyyMMdd}.log");

    internal void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.GetFiles(_logsDir, $"{_name}-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name[(name.LastIndexOf('-') + 1)..];
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var date) && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Where(c => !invalid.Contains(c)).ToArray();
        return new string(chars);
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _provider;
        private readonly string _category;

        public RollingFileLogger(RollingFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                // 打开即写即刷；每次写后清理过期日志（频率低，可接受）
                File.AppendAllText(_provider.ResolveTodayPath(), line + Environment.NewLine);
                _provider.CleanupOldLogs();
            }
            catch
            {
                // 日志失败不干扰主流程
            }
        }
    }
}

/// <summary>ILoggingBuilder 扩展：接入滚动文件日志。</summary>
public static class RollingFileLoggingExtensions
{
    public static ILoggingBuilder AddRollingFile(this ILoggingBuilder builder, string appRoot, string name, int retentionDays = 7)
    {
        builder.AddProvider(new RollingFileLoggerProvider(appRoot, name, retentionDays));
        return builder;
    }
}
