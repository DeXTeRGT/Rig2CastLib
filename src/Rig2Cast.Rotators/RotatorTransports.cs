using System.IO.Ports;
using System.Net.Sockets;

namespace Rig2Cast.Rotators;

public sealed class TcpRotatorTransport(string host, int port) : IRotatorTransport
{
    private TcpClient? _client;
    private NetworkStream Stream => _client?.GetStream() ?? throw new InvalidOperationException("Rotator TCP transport is not open.");
    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Stream.ReadAsync(buffer, cancellationToken);
    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class SerialRotatorTransport(string portName, int baudRate) : IRotatorTransport
{
    private SerialPort? _port;
    private Stream Stream => _port?.BaseStream ?? throw new InvalidOperationException("Rotator serial transport is not open.");
    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        { Handshake = Handshake.None, DtrEnable = false, RtsEnable = false };
        _port.Open();
        return ValueTask.CompletedTask;
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Stream.ReadAsync(buffer, cancellationToken);
    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
