namespace Rig2Cast.Protocols.Civ;

/// <summary>
/// Incrementally extracts bounded CI-V frames from an arbitrary byte stream.
/// Noise, incomplete malformed frames, and overlong frames are discarded while
/// the decoder continues looking for a fresh preamble.
/// </summary>
public sealed class CivFrameDecoder
{
    public const int MinimumFrameLength = 6;
    public const int DefaultMaximumFrameLength = 512;

    private readonly int _maximumFrameLength;
    private readonly List<byte> _frame = [];
    private int _preambleBytes;

    public CivFrameDecoder(int maximumFrameLength = DefaultMaximumFrameLength)
    {
        if (maximumFrameLength < MinimumFrameLength)
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrameLength),
                maximumFrameLength,
                $"Maximum frame length must be at least {MinimumFrameLength} bytes.");

        _maximumFrameLength = maximumFrameLength;
    }

    public IReadOnlyList<CivFrame> Append(ReadOnlySpan<byte> bytes)
    {
        List<CivFrame>? decoded = null;

        foreach (byte value in bytes)
        {
            if (_frame.Count == 0)
            {
                if (value == CivFrameCodec.Preamble)
                {
                    _preambleBytes++;
                    if (_preambleBytes >= 2)
                    {
                        _frame.Add(CivFrameCodec.Preamble);
                        _frame.Add(CivFrameCodec.Preamble);
                        _preambleBytes = 0;
                    }
                }
                else
                {
                    _preambleBytes = 0;
                }

                continue;
            }

            // CI-V interfaces may emit more than two fill/preamble bytes.
            if (_frame.Count == 2 && value == CivFrameCodec.Preamble)
                continue;

            _frame.Add(value);

            if (value == CivFrameCodec.Terminator)
            {
                if (_frame.Count >= MinimumFrameLength &&
                    !IsReservedFramingByte(_frame[2]) &&
                    !IsReservedFramingByte(_frame[3]))
                {
                    decoded ??= [];
                    decoded.Add(new CivFrame(
                        _frame[2],
                        _frame[3],
                        _frame.GetRange(4, _frame.Count - 5).ToArray()));
                }

                Reset();
            }
            else if (_frame.Count >= _maximumFrameLength)
            {
                Reset();
                if (value == CivFrameCodec.Preamble)
                    _preambleBytes = 1;
            }
        }

        return decoded is null ? Array.Empty<CivFrame>() : decoded;
    }

    public void Reset()
    {
        _frame.Clear();
        _preambleBytes = 0;
    }

    private static bool IsReservedFramingByte(byte value) =>
        value is CivFrameCodec.Preamble or CivFrameCodec.Terminator;
}
