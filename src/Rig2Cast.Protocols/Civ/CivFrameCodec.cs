namespace Rig2Cast.Protocols.Civ;

public static class CivFrameCodec
{
    public const byte Preamble = 0xFE;
    public const byte Terminator = 0xFD;

    public static byte[] Encode(CivFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var encoded = new byte[frame.Message.Length + 5];
        encoded[0] = Preamble;
        encoded[1] = Preamble;
        encoded[2] = frame.Destination;
        encoded[3] = frame.Source;
        frame.Message.Span.CopyTo(encoded.AsSpan(4));
        encoded[^1] = Terminator;
        return encoded;
    }
}
