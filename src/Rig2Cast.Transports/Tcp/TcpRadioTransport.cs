using System.Net.Sockets;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Transports.Tcp;

/// <summary>
/// Carries CAT bytes unchanged over a raw TCP stream. This transport does not
/// implement Telnet, RFC2217, rigctld, or any other application protocol.
/// </summary>
public sealed class TcpRadioTransport(TcpRadioTransportOptions options) : IRadioTransport
{
    private TcpClient? _client;
    private int _disposed;

    public string Id => $"tcp:{options.Host}:{options.Port}";

    public bool IsConnected => _client?.Connected == true;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConnected)
            return;
        ValidateOptions(options);

        var client = new TcpClient
        {
            NoDelay = options.NoDelay
        };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            await client.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _client, client, null) is not null)
                throw new InvalidOperationException("The TCP transport was connected concurrently.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException(
                $"TCP connection to {options.Host}:{options.Port} timed out after {options.ConnectTimeout}.", exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TcpClient? client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            try { client.Client.Shutdown(SocketShutdown.Both); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        GetStream().WriteAsync(data, cancellationToken);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        GetStream().ReadAsync(buffer, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await DisconnectAsync().ConfigureAwait(false);
    }

    private NetworkStream GetStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _client is { Connected: true } client
            ? client.GetStream()
            : throw new InvalidOperationException($"TCP transport '{Id}' is not connected.");
    }

    private static void ValidateOptions(TcpRadioTransportOptions value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Host);
        if (value.Port is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(value), value.Port, "TCP port must be from 1 through 65535.");
        if (value.ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(value), "TCP connect timeout must be positive.");
    }
}
