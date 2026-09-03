namespace Rig2Cast.Protocols.Civ;

/// <summary>CI-V packed BCD with the least-significant digit pair first.</summary>
public static class CivBcd
{
    public static byte[] Encode(long value, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ValidateByteCount(byteCount);

        var encoded = new byte[byteCount];
        long remaining = value;
        for (int index = 0; index < encoded.Length; index++)
        {
            int low = (int)(remaining % 10);
            remaining /= 10;
            int high = (int)(remaining % 10);
            remaining /= 10;
            encoded[index] = (byte)((high << 4) | low);
        }

        if (remaining != 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Value does not fit in {byteCount} BCD bytes.");
        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out long value)
    {
        value = 0;
        if (encoded.IsEmpty || encoded.Length > 9)
            return false;

        long multiplier = 1;
        foreach (byte packed in encoded)
        {
            int low = packed & 0x0F;
            int high = packed >> 4;
            if (low > 9 || high > 9)
            {
                value = 0;
                return false;
            }
            value += low * multiplier;
            multiplier *= 10;
            value += high * multiplier;
            multiplier *= 10;
        }
        return true;
    }

    public static byte[] EncodeBigEndian(long value, int byteCount)
    {
        byte[] encoded = Encode(value, byteCount);
        Array.Reverse(encoded);
        return encoded;
    }

    public static bool TryDecodeBigEndian(ReadOnlySpan<byte> encoded, out long value)
    {
        if (encoded.IsEmpty || encoded.Length > 9)
        {
            value = 0;
            return false;
        }

        Span<byte> littleEndian = stackalloc byte[encoded.Length];
        for (int index = 0; index < encoded.Length; index++)
            littleEndian[index] = encoded[encoded.Length - index - 1];
        return TryDecode(littleEndian, out value);
    }

    private static void ValidateByteCount(int byteCount)
    {
        if (byteCount is < 1 or > 9)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "BCD byte count must be between 1 and 9.");
    }
}
