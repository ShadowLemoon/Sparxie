using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sparxie.SessionHost.Hosting;
using Sparxie.SessionHost.Services;
using HostOptions = Sparxie.SessionHost.Hosting.HostOptions;

var options = HostOptions.FromEnvironment();

var builder = WebApplication.CreateBuilder();

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
