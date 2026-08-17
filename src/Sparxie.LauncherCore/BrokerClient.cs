using System.ComponentModel;
using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Sparxie.Contracts.Rpc;
using Sparxie.Infrastructure.Rpc;

namespace Sparxie.LauncherCore;

public sealed class BrokerClientOptions
{
    public string? BrokerExecutablePath { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>生产使用 Process.Start；测试可注入启动行为以验证 UAC 启动参数。</summary>
    public Func<ProcessStartInfo, Process?> ProcessStarter { get; init; } =
        static startInfo => Process.Start(startInfo);
}

/// <summary>
/// 无 UI 的 Broker 客户端：负责随机管道、UAC 启动、Ping、会话调用和单一事件流。
/// </summary>
public class BrokerClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly BrokerClientOptions _options;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private GrpcChannel? _channel;
    private SparxieBroker.SparxieBrokerClient? _client;
    private bool _disposed;

    public BrokerClient(string? pipeName = null, BrokerClientOptions? options = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? $"sparxie-{Guid.NewGuid():N}"
            : pipeName;
        if (!BrokerProcessArguments.IsValidPipeName(_pipeName))
        {
            throw new ArgumentException("Broker 管道名非法", nameof(pipeName));
        }

        _options = options ?? new BrokerClientOptions();
        if (_options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeout 必须大于零");
        }

        if (_options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryDelay 不能为负数");
        }
    }

    public string PipeName => _pipeName;

    public async Task<SparxieBroker.SparxieBrokerClient> EnsureConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return _client!;
            }

            StartBrokerElevated();
            var deadline = DateTime.UtcNow + _options.ConnectTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    return _client!;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                    _options.RetryDelay <= remaining ? _options.RetryDelay : remaining,
                    cancellationToken).ConfigureAwait(false);
            }

            throw new LauncherException(
                LauncherFailureKind.BrokerConnection,
                "无法在限定时间内连接 Sparxie Broker");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<PingResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.PingAsync(new PingRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<StartSessionResponse> StartSessionAsync(
        ProfileSnapshot profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.StartSessionAsync(new StartSessionRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Profile = profile,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<SetTargetFpsResponse> SetTargetFpsAsync(
        string sessionId,
        int targetFps,
        CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.SetTargetFpsAsync(new SetTargetFpsRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            TargetFps = targetFps,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerEventStream> OpenEventStreamAsync(
        CancellationToken cancellationToken = default)
    {
        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var call = client.StreamEvents(new StreamEventsRequest
        {
            ProtocolVersion = RpcContract.ProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
        }, cancellationToken: cancellationToken);

        // StreamEvents 在首个 SessionEvent 产生前不会返回 response headers；
        // 这里必须立即交还调用对象，才能继续发送 StartSession，避免握手自锁。
        return new BrokerEventStream(call);
    }

    /// <summary>兼容旧宿主的直接事件枚举；新宿主应使用 LauncherClient 的会话路由。</summary>
    public async IAsyncEnumerable<SessionEvent> StreamEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenEventStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var ev in stream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(TimeSpan.FromSeconds(1));
        var attemptToken = attemptCancellation.Token;

        GrpcChannel? channel = null;
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = (_, ct) =>
                    new NamedPipesConnectionFactory(_pipeName).ConnectAsync(ct),
            };
            channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler,
            });
            var client = new SparxieBroker.SparxieBrokerClient(channel);
            await client.PingAsync(new PingRequest
            {
                ProtocolVersion = RpcContract.ProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
            }, cancellationToken: attemptToken).ConfigureAwait(false);

            _channel = channel;
            _client = client;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            channel?.Dispose();
            return false;
        }
    }

    private void StartBrokerElevated()
    {
        var brokerExe = _options.BrokerExecutablePath
            ?? Path.Combine(AppContext.BaseDirectory, "Sparxie.Broker.exe");
        if (!File.Exists(brokerExe))
        {
            throw new LauncherException(
                LauncherFailureKind.BrokerConnection,
                $"未找到 Sparxie.Broker.exe: {brokerExe}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = brokerExe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = BrokerProcessArguments.Build(_pipeName),
            WorkingDirectory = Path.GetDirectoryName(brokerExe) ?? AppContext.BaseDirectory,
        };

        try
        {
            using var process = _options.ProcessStarter(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("UAC 启动 Broker 返回空进程");
            }
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new LauncherException(
                LauncherFailureKind.BrokerConnection,
                "用户取消了 Broker 的管理员权限请求",
                ex);
        }
        catch (Exception ex)
        {
            throw new LauncherException(
                LauncherFailureKind.BrokerConnection,
                $"UAC 启动 Broker 失败: {ex.Message}",
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client = null;
        _channel?.Dispose();
        _channel = null;
        _connectGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
