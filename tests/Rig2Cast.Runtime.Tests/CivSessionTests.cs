using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Protocols.Civ;
using Rig2Cast.Simulator;

namespace Rig2Cast.Runtime.Tests;

public sealed class CivSessionTests
{
    private static readonly CivFrame FrequencyQuery = new(0x94, 0xE0, [0x03]);

    [Fact]
    public async Task QueryMatchesReversedAddressesAndMessagePrefixAndConsumesEcho()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);

        Task<CivFrame> query = session.QueryAsync(FrequencyQuery, new byte[] { 0x03 }).AsTask();
        byte[] command = await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(CivFrameCodec.Encode(FrequencyQuery), command);
        await transport.SendRadioResponseAsync(command);
        await transport.SendRadioResponseAsync(new byte[] {
            0xFE, 0xFE, 0xE0, 0x94, 0x03, 0x00, 0x00, 0x25, 0x14, 0x00, 0xFD });

        CivFrame response = await query.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([0x03, 0x00, 0x00, 0x25, 0x14, 0x00], response.Message.ToArray());
    }

    [Fact]
    public async Task WrongAddressAndValidatorFailureAreRoutedAsUnsolicited()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);
        Task<CivFrame> query = session.QueryAsync(
            FrequencyQuery,
            new byte[] { 0x03 },
            frame => frame.Message.Length == 6).AsTask();
        await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await transport.SendRadioResponseAsync(
            new byte[] { 0xFE, 0xFE, 0xE0, 0x95, 0x03, 0x01, 0xFD });
        await transport.SendRadioResponseAsync(
            new byte[] { 0xFE, 0xFE, 0xE0, 0x94, 0x03, 0x01, 0xFD });
        await transport.SendRadioResponseAsync(new byte[] {
            0xFE, 0xFE, 0xE0, 0x94, 0x03, 0x00, 0x00, 0x25, 0x14, 0x00, 0xFD });

        await query.WaitAsync(TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<CivFrame> unsolicited = session
            .WatchUnsolicitedFramesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await unsolicited.MoveNextAsync());
        Assert.Equal(0x95, unsolicited.Current.Source);
        Assert.True(await unsolicited.MoveNextAsync());
        Assert.Equal([0x03, 0x01], unsolicited.Current.Message.ToArray());
    }

    [Fact]
    public async Task CommandAcceptsAcknowledgementAndRejectionDoesNotFaultSession()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);

        Task<CivFrame> accepted = session.CommandExpectingAcknowledgementAsync(
            new CivFrame(0x94, 0xE0, [0x05, 0x00])).AsTask();
        await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await transport.SendRadioResponseAsync(
            new byte[] { 0xFE, 0xFE, 0xE0, 0x94, 0xFB, 0xFD });
        Assert.Equal([0xFB], (await accepted.WaitAsync(TimeSpan.FromSeconds(1))).Message.ToArray());

        Task<CivFrame> rejected = session.CommandExpectingAcknowledgementAsync(
            new CivFrame(0x94, 0xE0, [0x05, 0x01])).AsTask();
        await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await transport.SendRadioResponseAsync(
            new byte[] { 0xFE, 0xFE, 0xE0, 0x94, 0xFA, 0xFD });
        await Assert.ThrowsAsync<CivCommandRejectedException>(() => rejected);

        await session.SendAsync(new CivFrame(0x94, 0xE0, [0x07]));
        Assert.NotEmpty(await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ResponseTimeoutFaultsSessionAgainstLateReplyReuse()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport, responseTimeout: TimeSpan.FromMilliseconds(50));

        Task<CivFrame> query = session.QueryAsync(FrequencyQuery, new byte[] { 0x03 }).AsTask();
        await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => query);
        await Assert.ThrowsAsync<RadioConnectionException>(
            () => session.QueryAsync(FrequencyQuery, new byte[] { 0x03 }).AsTask());
    }

    [Fact]
    public async Task CancellingCommittedQueryFaultsSession()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);
        using var cancellation = new CancellationTokenSource();

        Task<CivFrame> query = session.QueryAsync(
            FrequencyQuery, new byte[] { 0x03 }, cancellation.Token).AsTask();
        await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        await Assert.ThrowsAsync<RadioConnectionException>(
            () => session.SendAsync(new CivFrame(0x94, 0xE0, [0x07])).AsTask());
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesSessionUsable()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.QueryAsync(FrequencyQuery, new byte[] { 0x03 }, cancellation.Token).AsTask());

        await session.SendAsync(new CivFrame(0x94, 0xE0, [0x07]));
        Assert.NotEmpty(await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ConcurrentQueriesAreSerialized()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);

        Task<CivFrame> first = session.QueryAsync(FrequencyQuery, new byte[] { 0x03 }).AsTask();
        Task<CivFrame> second = session.QueryAsync(
            new CivFrame(0x94, 0xE0, [0x04]), new byte[] { 0x04 }).AsTask();
        Assert.NotEmpty(await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        await transport.SendRadioResponseAsync(
            new byte[] { 0xFE, 0xFE, 0xE0, 0x94, 0x03, 0x00, 0xFD });
        await first.WaitAsync(TimeSpan.FromSeconds(1));

        byte[] secondCommand = await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0x04, secondCommand[4]);
        await transport.SendRadioResponseAsync(
            new byte[] { 0xFE, 0xFE, 0xE0, 0x94, 0x04, 0x01, 0xFD });
        await second.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RemoteCloseFailsPendingQueryAndFaultsSession()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(transport);

        Task<CivFrame> query = session.QueryAsync(FrequencyQuery, new byte[] { 0x03 }).AsTask();
        await transport.ReadDriverCommandAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await transport.SendRadioResponseAsync(ReadOnlyMemory<byte>.Empty);

        await Assert.ThrowsAsync<RadioConnectionException>(() => query);
        await Assert.ThrowsAsync<RadioConnectionException>(
            () => session.SendAsync(new CivFrame(0x94, 0xE0, [0x07])).AsTask());
    }

    [Fact]
    public async Task UnsolicitedOverflowIsCounted()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var session = new CivSession(
            transport, new CivSessionOptions { UnsolicitedCapacity = 2 });

        for (byte value = 0; value < 4; value++)
        {
            await transport.SendRadioResponseAsync(
                CivFrameCodec.Encode(new CivFrame(0x00, 0x94, [0x03, value])));
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        while (session.DroppedUnsolicitedFrameCount == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(session.ConsumeDroppedUnsolicitedFrameCount() > 0);
        Assert.Equal(0, session.DroppedUnsolicitedFrameCount);
    }

    [Fact]
    public async Task SessionValidatesConnectedTransportAndOptions()
    {
        var disconnected = new InMemoryRadioTransport();
        Assert.Throws<InvalidOperationException>(() => new CivSession(disconnected));

        var connected = new InMemoryRadioTransport();
        await connected.ConnectAsync();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CivSession(
            connected, new CivSessionOptions { ReadBufferLength = 0 }));
        await connected.DisposeAsync();
    }

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync()
    {
        var transport = new InMemoryRadioTransport("CI-V test");
        await transport.ConnectAsync();
        return transport;
    }
}
