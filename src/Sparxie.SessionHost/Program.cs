using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sparxie.Infrastructure.Logging;
using Sparxie.SessionHost.Hosting;
using Sparxie.SessionHost.Services;
using HostOptions = Sparxie.SessionHost.Hosting.HostOptions;

var options = HostOptions.FromEnvironment();

// 全局异常兜底：未处理/未观察异常写诊断文件（崩溃前可定位），不静默 failfast。
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "logs", $"host-crash-{DateTime.Now:yyyyMMddHHmmss}.log"),
            $"[{DateTime.Now:O}] Unhandled: {e.ExceptionObject}\n");
    }
    catch
    {
    }
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try
    {
        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "logs", $"host-crash-{DateTime.Now:yyyyMMddHHmmss}.log"),
            $"[{DateTime.Now:O}] Unobserved: {e.Exception}\n");
    }
    catch
    {
    }
    e.SetObserved();
};

var builder = WebApplication.CreateBuilder();

// 结构化滚动日志：logs/host-*.log，保留 7 天
builder.Logging.AddRollingFile(AppContext.BaseDirectory, "host");

// Host 是单会话进程：会话结束（Exited/Failed）后立即退出，
// 不等待 gRPC 连接优雅关闭的默认 30 秒超时。
builder.Host.ConfigureHostOptions(hostOptions =>
{
    hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(3);
});

// 私有管道默认 CurrentUserOnly：仅当前用户 SID 可连接。
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenNamedPipe(options.HostPipeName, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<HostService>();

await app.RunAsync();
return 0;
