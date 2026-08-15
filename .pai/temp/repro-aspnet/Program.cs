using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();

[DllImport("HoyoTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
static extern int hoyo_get_abi_version(out uint version, out uint size);

Console.WriteLine($"[repro-aspnet] pid={Environment.ProcessId} serverGC={System.Runtime.GCSettings.IsServerGC}");

app.MapGet("/", () =>
{
    try
    {
        var rc = hoyo_get_abi_version(out var ver, out var size);
        return $"[repro-aspnet] rc={rc} version={ver} size={size}";
    }
    catch (Exception ex)
    {
        return $"[repro-aspnet] EXCEPTION: {ex}";
    }
});

// 进程启动后立即在同一线程触发 P/Invoke（模拟 SessionHost 行为）
try
{
    var rc = hoyo_get_abi_version(out var ver, out var size);
    Console.WriteLine($"[repro-aspnet] load-on-startup rc={rc} version={ver} size={size}");
}
catch (Exception ex)
{
    Console.WriteLine($"[repro-aspnet] load-on-startup EXCEPTION: {ex}");
}

await app.RunAsync();
