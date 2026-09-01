namespace Rig2Cast.Protocols.Ascii;

public sealed record AsciiCatSessionOptions
{
    public required string ProtocolName { get; init; }

    public required Func<string, string> FrameCommand { get; init; }

    public required Action<string> ValidateResponsePrefix { get; init; }

    public required Func<string, Exception> InvalidFrameException { get; init; }

    public Func<string, string, Exception?> CommandRejection { get; init; } = static (_, _) => null;

    public int MaximumFrameLength { get; init; } = 512;

    public int ReadBufferLength { get; init; } = 256;

    public int UnsolicitedCapacity { get; init; } = 256;

    public TimeSpan DefaultResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
