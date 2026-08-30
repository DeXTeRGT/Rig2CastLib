using System.IO.Ports;

namespace Rig2Cast.Transports.Serial;

public sealed record SerialRadioTransportOptions
{
    public required string PortName { get; init; }

    public int BaudRate { get; init; } = 38_400;

    public int DataBits { get; init; } = 8;

    public StopBits StopBits { get; init; } = StopBits.Two;

    public Parity Parity { get; init; } = Parity.None;

    public Handshake Handshake { get; init; } = Handshake.RequestToSend;

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
