using System.Threading.Channels;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Simulator;

public sealed class InMemoryRadioTransport(string id = "simulated") : IRadioTransport
{
    private readonly Channel<byte[]> _radioToDriver = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _driverToRadio = Channel.CreateUnbounded<byte[]>();
    private byte[]? _pendingRead;
    private int _pendingOffset;

    public string Id { get; } = id;

    public bool IsConnected { get; private set; }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return _driverToRadio.Writer.WriteAsync(data.ToArray(), cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_pendingRead is null)
        {
            _pendingRead = await _radioToDriver.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _pendingOffset = 0;
        }

        int count = Math.Min(buffer.Length, _pendingRead.Length - _pendingOffset);
        _pendingRead.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        if (_pendingOffset == _pendingRead.Length)
        {
            _pendingRead = null;
            _pendingOffset = 0;
        }

        return count;
    }

    public ValueTask<byte[]> ReadDriverCommandAsync(CancellationToken cancellationToken = default) =>
        _driverToRadio.Reader.ReadAsync(cancellationToken);

    public ValueTask SendRadioResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _radioToDriver.Writer.WriteAsync(data.ToArray(), cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _radioToDriver.Writer.TryComplete();
        _driverToRadio.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The simulated transport is not connected.");
        }
    }
}
