namespace Rig2Cast.Protocols.Civ;

public sealed record CivSessionOptions
{
    public int MaximumFrameLength { get; init; } = CivFrameDecoder.DefaultMaximumFrameLength;

    public int ReadBufferLength { get; init; } = 256;

    public int UnsolicitedCapacity { get; init; } = 256;

    public TimeSpan DefaultResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
