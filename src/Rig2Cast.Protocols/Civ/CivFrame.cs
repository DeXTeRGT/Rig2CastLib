namespace Rig2Cast.Protocols.Civ;

/// <summary>
/// One decoded CI-V message. <see cref="Message"/> contains the command byte and
/// any command-specific data, but not the preamble, addresses, or terminator.
/// </summary>
public sealed class CivFrame
{
    private readonly byte[] _message;

    public CivFrame(byte destination, byte source, ReadOnlySpan<byte> message)
    {
        ValidateAddress(destination, nameof(destination));
        ValidateAddress(source, nameof(source));
        if (message.IsEmpty)
            throw new ArgumentException("A CI-V frame must contain a command or response byte.", nameof(message));
        if (message.Contains(CivFrameCodec.Terminator))
            throw new ArgumentException("A CI-V message cannot contain the frame terminator.", nameof(message));

        Destination = destination;
        Source = source;
        _message = message.ToArray();
    }

    public byte Destination { get; }

    public byte Source { get; }

    public ReadOnlyMemory<byte> Message => _message;

    private static void ValidateAddress(byte address, string parameterName)
    {
        if (address is CivFrameCodec.Preamble or CivFrameCodec.Terminator)
            throw new ArgumentOutOfRangeException(
                parameterName,
                address,
                "CI-V preamble and terminator bytes cannot be used as addresses.");
    }
}
