using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace ManualHostCheck;

internal static class Program
{
    private static async Task<int> Main()
    {
        var pipeName = "sparxie-host-manual-" + Guid.NewGuid().ToString("N");
        var hostExe = @"D:\Code\Sparxie\src\Sparxie.SessionHost\bin\Debug\net10.0-windows\Sparxie.SessionHost.exe";

        var psi = new ProcessStartInfo
        {
            FileName = hostExe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostExe)!,
        };
        psi.Environment[HostEnvironment.SessionId] = "manual";
        psi.Environment[HostEnvironment.HostPipeName] = pipeName;
        psi.Environment[HostEnvironment.ProfileJson] = ProfileJson.Format(new ProfileSnapshot
        {
            ProfileId = "p1",
            DisplayName = "x",
            Game = "starRail",
            Variant = "intl",
            ExecutablePath = @"D:\nonexistent\StarRail.exe",
            Hoyo = new HoyoSettings
            {
                FpsUnlockEnabled = true,
                TargetFps = 120,
                BackgroundFps = 10,
                ProcessPriority = "normal",
                GenshinPreset30Fps = 60,
                GenshinPreset60Fps = 1000,
                GenshinTouchUiScalePercent = 400,
            },
        });

        var host = Process.Start(psi)!;
        Console.WriteLine($"host started pid={host.Id}");

        // 探测管道就绪
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var probe = await new NamedPipesConnectionFactory(pipeName).ConnectAsync();
                Console.WriteLine($"pipe ready after {i * 250}ms");
                break;
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, ct) => new NamedPipesConnectionFactory(pipeName).ConnectAsync(ct),
        };
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        var client = new SparxieHost.SparxieHostClient(channel);
        using var call = client.Connect();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var ev in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                Console.WriteLine($"EVENT state={ev.State} code={ev.ErrorCode} msg={ev.Message}");
            }

            Console.WriteLine("stream ended normally");
        }
        catch (Exception ex)
        {
            Console.WriteLine("stream error: " + ex.GetType().Name + ": " + ex.Message);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !host.HasExited)
        {
            await Task.Delay(200);
        }

        Console.WriteLine("host exited: " + host.HasExited);
        if (!host.HasExited)
        {
            host.Kill(entireProcessTree: true);
        }

        await channel.ShutdownAsync();
        return 0;
    }
}
