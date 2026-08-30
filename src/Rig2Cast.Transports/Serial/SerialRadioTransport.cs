using System.IO.Ports;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Transports.Serial;

public sealed class SerialRadioTransport(SerialRadioTransportOptions options) : IRadioTransport
{
    private SerialPort? _port;
    private int _disposed;

    public string Id => $"serial:{options.PortName}";

    public bool IsConnected => _port?.IsOpen == true;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConnected)
        {
            return ValueTask.CompletedTask;
        }

        ValidateOptions(options);
        var port = new SerialPort(
            options.PortName,
            options.BaudRate,
            options.Parity,
            options.DataBits,
            options.StopBits)
        {
            Handshake = options.Handshake,
            ReadTimeout = ToMilliseconds(options.ReadTimeout),
            WriteTimeout = ToMilliseconds(options.WriteTimeout),
            Encoding = System.Text.Encoding.ASCII
        };

        try
        {
            port.Open();
            _port = port;
        }
        catch
        {
            port.Dispose();
            throw;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SerialPort? port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            if (port.IsOpen)
            {
                port.Close();
            }

            port.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        SerialPort port = GetOpenPort();
        await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        GetOpenPort().BaseStream.ReadAsync(buffer, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    private SerialPort GetOpenPort()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _port is { IsOpen: true } port
            ? port
            : throw new InvalidOperationException($"Serial port '{options.PortName}' is not connected.");
    }

    private static void ValidateOptions(SerialRadioTransportOptions value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.PortName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.BaudRate);
        if (value.DataBits is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Serial data bits must be between 5 and 8.");
        }
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return (int)timeout.TotalMilliseconds;
    }
}
