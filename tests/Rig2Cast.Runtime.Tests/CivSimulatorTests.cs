using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Protocols.Civ;
using Rig2Cast.Simulator;
using Rig2Cast.Simulator.Civ;

namespace Rig2Cast.Runtime.Tests;

public sealed class CivSimulatorTests
{
    [Theory]
    [InlineData(0, 1, "00")]
    [InlineData(14_250_000, 5, "0000251400")]
    [InlineData(74_800_000, 5, "0000807400")]
    public void BcdRoundTripsCanonicalLittleEndianValues(long value, int width, string expectedHex)
    {
        byte[] encoded = CivBcd.Encode(value, width);

        Assert.Equal(expectedHex, Convert.ToHexString(encoded));
        Assert.True(CivBcd.TryDecode(encoded, out long decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void BcdRejectsInvalidWidthOverflowAndDigits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CivBcd.Encode(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CivBcd.Encode(100, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CivBcd.Encode(1, 0));
        Assert.False(CivBcd.TryDecode([], out _));
        Assert.False(CivBcd.TryDecode([0x1A], out _));
        Assert.False(CivBcd.TryDecode(new byte[10], out _));
    }

    [Theory]
    [InlineData(0, "0000")]
    [InlineData(143, "0143")]
    [InlineData(255, "0255")]
    public void BigEndianBcdRoundTripsLevelValues(long value, string expectedHex)
    {
        byte[] encoded = CivBcd.EncodeBigEndian(value, 2);

        Assert.Equal(expectedHex, Convert.ToHexString(encoded));
        Assert.True(CivBcd.TryDecodeBigEndian(encoded, out long decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public async Task SimulatorAnswersFrequencyWithEchoAndSingleByteFragments()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(
            transport,
            new CivSimulatorOptions
            {
                InitialFrequencyHz = 14_250_000,
                EchoCommands = true,
                ResponseFragmentLength = 1
            });
        await using var session = new CivSession(transport);

        CivFrame response = await session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x03]),
            new byte[] { 0x03 },
            frame => frame.Message.Length == 6);

        Assert.True(CivBcd.TryDecode(response.Message.Span[1..], out long frequency));
        Assert.Equal(14_250_000, frequency);
    }

    [Fact]
    public async Task SimulatorAnswersModeAndFilterWithoutEcho()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(
            transport,
            new CivSimulatorOptions { InitialMode = 0x03, InitialFilter = 0x02, EchoCommands = false });
        await using var session = new CivSession(transport);

        CivFrame response = await session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x04]), new byte[] { 0x04 });

        Assert.Equal([0x04, 0x03, 0x02], response.Message.ToArray());
    }

    [Fact]
    public async Task SimulatorAcceptsSplitMutationAndReturnsUpdatedState()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport);

        await session.CommandExpectingAcknowledgementAsync(
            new CivFrame(0x94, 0xE0, [0x0F, 0x01]));
        CivFrame response = await session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x0F]), new byte[] { 0x0F });

        Assert.Equal([0x0F, 0x01], response.Message.ToArray());
    }

    [Fact]
    public async Task SimulatorAcceptsPttMutationAndReturnsUpdatedState()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport);

        await session.CommandExpectingAcknowledgementAsync(
            new CivFrame(0x94, 0xE0, [0x1C, 0x00, 0x01]));
        CivFrame response = await session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x1C, 0x00]), new byte[] { 0x1C, 0x00 });

        Assert.Equal([0x1C, 0x00, 0x01], response.Message.ToArray());
    }

    [Fact]
    public async Task SimulatorAcceptsPassbandMutationAndReturnsBcdIndex()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport);

        await session.CommandExpectingAcknowledgementAsync(
            new CivFrame(0x94, 0xE0, [0x1A, 0x03, 0x31]));
        CivFrame response = await session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x1A, 0x03]), new byte[] { 0x1A, 0x03 });

        Assert.Equal([0x1A, 0x03, 0x31], response.Message.ToArray());
    }

    [Fact]
    public async Task UnsupportedAndInjectedRejectionsAreExplicit()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport);

        await Assert.ThrowsAsync<CivCommandRejectedException>(() => session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x19]), new byte[] { 0x19 }).AsTask());

        simulator.SetNextResponse(CivSimulatorNextResponse.Reject);
        await Assert.ThrowsAsync<CivCommandRejectedException>(() => session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x03]), new byte[] { 0x03 }).AsTask());
    }

    [Fact]
    public async Task DroppedResponseExercisesTerminalTimeout()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport, responseTimeout: TimeSpan.FromMilliseconds(50));
        simulator.SetNextResponse(CivSimulatorNextResponse.Drop);

        await Assert.ThrowsAsync<TimeoutException>(() => session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x03]), new byte[] { 0x03 }).AsTask());
        await Assert.ThrowsAsync<RadioConnectionException>(() => session.SendAsync(
            new CivFrame(0x94, 0xE0, [0x07])).AsTask());
    }

    [Fact]
    public async Task InjectedCloseExercisesConnectionFailure()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport);
        simulator.SetNextResponse(CivSimulatorNextResponse.Close);

        await Assert.ThrowsAsync<RadioConnectionException>(() => session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x03]), new byte[] { 0x03 }).AsTask());
    }

    [Fact]
    public async Task StateChangesProduceTransceiveBroadcastsAndAffectLaterReads()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using var session = new CivSession(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<CivFrame> unsolicited = session
            .WatchUnsolicitedFramesAsync(timeout.Token).GetAsyncEnumerator();

        await simulator.EmitFrequencyTransceiveAsync(7_100_000);
        Assert.True(await unsolicited.MoveNextAsync());
        Assert.Equal(0x00, unsolicited.Current.Destination);
        Assert.Equal(0x00, unsolicited.Current.Message.Span[0]);
        Assert.True(CivBcd.TryDecode(unsolicited.Current.Message.Span[1..], out long announced));
        Assert.Equal(7_100_000, announced);

        await simulator.EmitModeTransceiveAsync(0x00, 0x03);
        Assert.True(await unsolicited.MoveNextAsync());
        Assert.Equal([0x01, 0x00, 0x03], unsolicited.Current.Message.ToArray());

        CivFrame frequency = await session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x03]), new byte[] { 0x03 });
        Assert.True(CivBcd.TryDecode(frequency.Message.Span[1..], out long queried));
        Assert.Equal(7_100_000, queried);
    }

    [Fact]
    public async Task SimulatorValidatesTransportAndOptions()
    {
        var disconnected = new InMemoryRadioTransport();
        Assert.Throws<InvalidOperationException>(() => new CivRadioSimulator(disconnected));
        await disconnected.DisposeAsync();

        await using var connected = await ConnectedTransportAsync();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CivRadioSimulator(
            connected, new CivSimulatorOptions { ResponseFragmentLength = 0 }));
    }

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync()
    {
        var transport = new InMemoryRadioTransport("IC-7300 CI-V simulator test");
        await transport.ConnectAsync();
        return transport;
    }
}
