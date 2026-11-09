﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sparxie.Broker.Hosting;
using Sparxie.Broker.Services;
using Sparxie.Broker.Sessions;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Logging;

string? pipeName;
int? parentProcessId = null;
if (args.Length == 0)
{
    // 仅供非提权测试/调试进程使用；生产 Launcher 通过受控命令行参数传递。
    pipeName = Environment.GetEnvironmentVariable("SPARXIE_PIPE_NAME");
}
else if (!BrokerProcessArguments.TryParse(args, out pipeName, out parentProcessId, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    return 2;
}

if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("未提供 Broker 管道名");
    return 2;
}

var builder = WebApplication.CreateBuilder();

// 结构化滚动日志：logs/broker-*.log，保留 7 天
builder.Logging.AddRollingFile(Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve(), "broker");

// Kestrel 命名管道默认 CurrentUserOnly 使用的是令牌的 Owner SID。提权（UAC）后的管理员令牌 Owner 是
// BUILTIN\Administrators，而非提权前的普通令牌里该组仅为 deny-only，于是同一用户的非提权 Launcher/桌面端
// 会被拒绝连接（表现为 Broker 空等 10 秒后因无控制流而退出）。这里改为显式 ACL：只允许当前用户 SID。
builder.WebHost.UseNamedPipes(options =>
{
    options.CurrentUserOnly = false;
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法取得当前用户 SID");
        var security = new System.IO.Pipes.PipeSecurity();
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            user,
            System.IO.Pipes.PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        options.PipeSecurity = security;
    }
});

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenNamedPipe(pipeName, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton(new BrokerLifecycleOptions { Enabled = true });

var app = builder.Build();
app.MapGrpcService<BrokerService>();
var registry = app.Services.GetRequiredService<SessionRegistry>();
var lifecycle = app.Services.GetRequiredService<BrokerLifecycleOptions>();

// 未建立控制流的孤立 Broker 不长期驻留；控制流建立后由 BrokerService 按活动 Host 管理收尾。
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(lifecycle.IdleBeforeControl, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            if (!registry.HasControlStream && !registry.HasActiveSessions)
            {
                app.Lifetime.StopApplication();
            }
        }
        catch (OperationCanceledException)
        {
        }
    });
});

// 生命周期绑定（后台 RPC 进程）：由主程序传入其 PID，主程序一退出 Broker 就跟着退出，
// 不留下隐藏的提权后台进程。已进入 Running 的游戏进程不受影响（Host 与游戏的关系不变）。
if (parentProcessId is { } parentPid)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                await parent.WaitForExitAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // 父进程已经不存在：立即退出。
            }
            catch (OperationCanceledException)
            {
                return;
            }

            app.Lifetime.StopApplication();
        });
    });
}

await app.RunAsync();
return 0;
