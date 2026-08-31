using System.IO.Ports;

namespace Rig2Cast.Transports.Serial;

public sealed record SerialRadioTransportOptions
{
    public required string PortName { get; init; }

    public required int BaudRate { get; init; }

    public int DataBits { get; init; } = 8;

    public StopBits StopBits { get; init; } = StopBits.One;

    public Parity Parity { get; init; } = Parity.None;

    public Handshake Handshake { get; init; } = Handshake.None;

    public bool DtrEnable { get; init; }

    public bool RtsEnable { get; init; }

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
