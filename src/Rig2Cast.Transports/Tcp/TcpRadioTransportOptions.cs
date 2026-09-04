namespace Rig2Cast.Transports.Tcp;

public sealed record TcpRadioTransportOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool NoDelay { get; init; } = true;

    public bool KeepAlive { get; init; } = true;
}
