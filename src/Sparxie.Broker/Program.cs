using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sparxie.Broker.Services;
using Sparxie.Infrastructure.Zzz;

var pipeName = Environment.GetEnvironmentVariable("SPARXIE_PIPE_NAME");
if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("SPARXIE_PIPE_NAME 未设置，Broker 拒绝启动");
    return 1;
}

var builder = WebApplication.CreateBuilder();

// Kestrel 命名管道默认带 PipeOptions.CurrentUserOnly：仅当前用户 SID 可连接，
// 满足“仅允许当前用户”的 ACL 要求，无需额外 PipeSecurity。
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenNamedPipe(pipeName, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<Sparxie.Broker.Sessions.SessionRegistry>();

var app = builder.Build();
app.MapGrpcService<BrokerService>();

// 双重故障兜底：上次 Broker 与 Host 同时不可用时遗留的 ZZZ 恢复记录，
// 在本次 Broker 启动时先恢复 PC 配置。恢复失败时保留资产并阻止后续 ZZZ 会话。
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ZzzRecoveryBootstrap");
    foreach (var record in ZzzRecoveryStore.FindAll())
    {
        try
        {
            ZzzRecoveryStore.Restore(record);
            logger.LogInformation("启动时已恢复遗留 ZZZ 配置: session={SessionId}", record.SessionId);
        }
        catch (Exception ex)
        {
            // 游戏仍运行、备份损坏或路径越界：保留资产，由 ZZZ 启动前校验再次阻止。
            logger.LogError(ex, "启动时恢复遗留 ZZZ 配置失败: session={SessionId}", record.SessionId);
        }
    }
}

await app.RunAsync();
return 0;
