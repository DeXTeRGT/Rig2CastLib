using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Runtime.Tests;

public sealed class CivFrameDecoderTests
{
    [Fact]
    public void CodecProducesCanonicalAddressedFrame()
    {
        var frame = new CivFrame(0x94, 0xE0, [0x03]);

        Assert.Equal([0xFE, 0xFE, 0x94, 0xE0, 0x03, 0xFD], CivFrameCodec.Encode(frame));
    }

    [Fact]
    public void FrameOwnsItsMessageAndRejectsInvalidMessages()
    {
        byte[] message = [0x03, 0x12];
        var frame = new CivFrame(0x94, 0xE0, message);
        message[0] = 0x04;

        Assert.Equal([0x03, 0x12], frame.Message.ToArray());
        Assert.Throws<ArgumentException>(() => new CivFrame(0x94, 0xE0, []));
        Assert.Throws<ArgumentException>(() => new CivFrame(0x94, 0xE0, [0x03, 0xFD]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CivFrame(0xFE, 0xE0, [0x03]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CivFrame(0x94, 0xFD, [0x03]));
    }

    [Fact]
    public void DecoderAcceptsEveryPossibleFragmentBoundary()
    {
        byte[] encoded = [0xFE, 0xFE, 0xE0, 0x94, 0x03, 0x12, 0x34, 0xFD];

        for (int boundary = 0; boundary <= encoded.Length; boundary++)
        {
            var decoder = new CivFrameDecoder();
            var frames = decoder.Append(encoded.AsSpan(0, boundary))
                .Concat(decoder.Append(encoded.AsSpan(boundary)))
                .ToArray();

            CivFrame frame = Assert.Single(frames);
            Assert.Equal(0xE0, frame.Destination);
            Assert.Equal(0x94, frame.Source);
            Assert.Equal([0x03, 0x12, 0x34], frame.Message.ToArray());
        }
    }

    [Fact]
    public void DecoderExtractsConcatenatedFrames()
    {
        var decoder = new CivFrameDecoder();

        IReadOnlyList<CivFrame> frames = decoder.Append([
            0xFE, 0xFE, 0xE0, 0x94, 0xFB, 0xFD,
            0xFE, 0xFE, 0xE0, 0x94, 0xFA, 0xFD]);

        Assert.Equal(2, frames.Count);
        Assert.Equal([0xFB], frames[0].Message.ToArray());
        Assert.Equal([0xFA], frames[1].Message.ToArray());
    }

    [Fact]
    public void DecoderResynchronizesAfterNoiseExtraPreamblesAndMalformedFrame()
    {
        var decoder = new CivFrameDecoder();

        IReadOnlyList<CivFrame> frames = decoder.Append([
            0x00, 0x55, 0xFE,
            0xFE, 0xFE, 0xFD,
            0xFE, 0xFE, 0x94, 0xFE, 0x03, 0xFD,
            0x19, 0xFE, 0xFE, 0xFE, 0x94, 0xE0, 0x04, 0xFD]);

        CivFrame frame = Assert.Single(frames);
        Assert.Equal(0x94, frame.Destination);
        Assert.Equal(0xE0, frame.Source);
        Assert.Equal([0x04], frame.Message.ToArray());
    }

    [Fact]
    public void DecoderDropsOverlongFrameAndFindsNextPreamble()
    {
        var decoder = new CivFrameDecoder(maximumFrameLength: 7);

        IReadOnlyList<CivFrame> frames = decoder.Append([
            0xFE, 0xFE, 0x94, 0xE0, 0x03, 0x01, 0x02, 0x03,
            0xFE, 0xFE, 0xE0, 0x94, 0xFB, 0xFD]);

        CivFrame frame = Assert.Single(frames);
        Assert.Equal([0xFB], frame.Message.ToArray());
    }

    [Fact]
    public void DecoderValidatesMaximumFrameLengthAndCanBeReset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CivFrameDecoder(5));

        var decoder = new CivFrameDecoder();
        Assert.Empty(decoder.Append([0xFE, 0xFE, 0x94]));
        decoder.Reset();
        CivFrame frame = Assert.Single(decoder.Append([0xFE, 0xFE, 0xE0, 0x94, 0xFB, 0xFD]));
        Assert.Equal([0xFB], frame.Message.ToArray());
    }
}
