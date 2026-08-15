using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;
using Sparxie.Broker.Services;

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

await app.RunAsync();
return 0;
