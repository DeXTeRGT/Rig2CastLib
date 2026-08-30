namespace Rig2Cast.Abstractions.Transports;

public interface IRadioTransport : IAsyncDisposable
{
    string Id { get; }

    bool IsConnected { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
