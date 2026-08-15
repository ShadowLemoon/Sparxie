using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace Sparxie.Infrastructure.Rpc;

/// <summary>官方推荐的命名管道 gRPC 连接工厂（SocketsHttpHandler.ConnectCallback）。</summary>
public sealed class NamedPipesConnectionFactory
{
    private readonly string _pipeName;

    public NamedPipesConnectionFactory(string pipeName)
    {
        _pipeName = pipeName;
    }

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var stream = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.WriteThrough | PipeOptions.Asynchronous,
            impersonationLevel: TokenImpersonationLevel.Anonymous);

        try
        {
            await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
