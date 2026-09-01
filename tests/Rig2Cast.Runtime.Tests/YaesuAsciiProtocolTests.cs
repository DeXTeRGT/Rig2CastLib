using System.Text;
using System.Threading.Channels;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Yaesu.Protocol;

namespace Rig2Cast.Runtime.Tests;

public sealed class YaesuAsciiProtocolTests
{
    [Theory]
    [InlineData("fa", "FA;")]
    [InlineData("FA014250000", "FA014250000;")]
    [InlineData("tx0;", "TX0;")]
    public void FrameNormalizesCommand(string command, string expected) =>
        Assert.Equal(expected, YaesuAsciiProtocol.Frame(command));

    [Fact]
    public async Task QueryWritesFramedCommandAndReadsTerminatedResponse()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;", "FA014250000;");
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        string response = await protocol.QueryAsync("FA", "FA");

        Assert.Equal("FA014250000;", response);
        transport.AssertComplete();
    }

    [Fact]
    public async Task QueryRejectsUnexpectedResponsePrefix()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;", "FB007100000;");
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => protocol.QueryAsync("FA", "FA", timeout.Token).AsTask());

        using var watchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<string> frames = protocol
            .WatchUnsolicitedFramesAsync(watchTimeout.Token)
            .GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("FB007100000;", frames.Current);
    }

    [Fact]
    public async Task QueryRoutesUnsolicitedFrameBeforeMatchingResponse()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;", "FB007100000;FA014250000;");
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        string response = await protocol.QueryAsync("FA", "FA");

        Assert.Equal("FA014250000;", response);
        using var watchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<string> frames = protocol
            .WatchUnsolicitedFramesAsync(watchTimeout.Token)
            .GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("FB007100000;", frames.Current);
    }

    [Fact]
    public async Task QueryValidatorRejectsSamePrefixFrameWithWrongShape()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;", "FA1;FA014250000;");
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        string response = await protocol.QueryAsync("FA", "FA", frame => frame.Length == 12);

        Assert.Equal("FA014250000;", response);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<string> frames = protocol
            .WatchUnsolicitedFramesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("FA1;", frames.Current);
    }

    [Fact]
    public async Task QueryDecodesResponseSplitAcrossReads()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;", "FA014", "250000;");
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        Assert.Equal("FA014250000;", await protocol.QueryAsync("FA", "FA"));
    }

    [Fact]
    public async Task ResponseTimeoutFaultsSessionToPreventLateResponseMisrouting()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;");
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => protocol.QueryAsync("FA", "FA").AsTask());
        await Assert.ThrowsAsync<RadioConnectionException>(() => protocol.SendAsync("VS0").AsTask());
    }

    [Fact]
    public async Task CallerCancellationDoesNotInterruptStartedFrameWrite()
    {
        var transport = new BlockingWriteRadioTransport();
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);
        using var cancellation = new CancellationTokenSource();

        Task send = protocol.SendAsync("FA014250000", cancellation.Token).AsTask();
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        Assert.False(send.IsCompleted);
        transport.CompleteWrite();
        await send;
        Assert.Equal("FA014250000;", transport.WrittenFrame);
    }

    [Fact]
    public async Task SamePrefixFrameDuringWriteCannotCompleteQuery()
    {
        var transport = new BlockingWriteRadioTransport();
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        Task<string> query = protocol.QueryAsync("FA", "FA").AsTask();
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));
        await transport.EmitAsync("FA007100000;");
        using var watchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<string> frames = protocol
            .WatchUnsolicitedFramesAsync(watchTimeout.Token).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("FA007100000;", frames.Current);
        Assert.False(query.IsCompleted);

        transport.CompleteWrite();
        await Task.Delay(20);
        await transport.EmitAsync("FA014250000;");
        Assert.Equal("FA014250000;", await query.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task UnsolicitedOverflowIsCountedRatherThanRemainingSilent()
    {
        var transport = new ScriptedRadioTransport();
        await transport.ConnectAsync();
        await using var protocol = new YaesuAsciiProtocol(transport);

        for (int index = 0; index < 300; index++)
            await transport.EmitAsync($"FA{index:D9};");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        while (protocol.DroppedUnsolicitedFrameCount == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(protocol.DroppedUnsolicitedFrameCount > 0);
        Assert.True(protocol.ConsumeDroppedUnsolicitedFrameCount() > 0);
        Assert.Equal(0, protocol.DroppedUnsolicitedFrameCount);
    }
}

internal sealed class ScriptedRadioTransport(bool ignoreReadCancellation = false) : IRadioTransport
{
    private readonly Queue<(string Command, byte[][] Responses)> _script = new();
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
    private byte[]? _response;
    private int _responseOffset;
    private int _disposeCount;

    public string Id => "scripted";

    public bool IsConnected { get; private set; }
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void Add(string command, params string[] responses) =>
        _script.Enqueue((command, responses.Select(Encoding.ASCII.GetBytes).ToArray()));

    public ValueTask EmitAsync(string frame, CancellationToken cancellationToken = default) =>
        _responses.Writer.WriteAsync(Encoding.ASCII.GetBytes(frame), cancellationToken);

    public void AssertComplete()
    {
        Assert.Empty(_script);
        Assert.True(_response is null || _responseOffset == _response.Length);
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(IsConnected);
        string command = Encoding.ASCII.GetString(data.Span);
        if (command == "RVM;" && (_script.Count == 0 || _script.Peek().Command != command))
        {
            _ = EnqueueResponsesAfterWriteAsync([Encoding.ASCII.GetBytes("RVM05.66;")]);
            return ValueTask.CompletedTask;
        }
        Assert.NotEmpty(_script);
        (string expected, byte[][] responses) = _script.Dequeue();
        Assert.Equal(expected, command);
        Assert.Null(_response);
        if (responses.Length > 0)
            _ = EnqueueResponsesAfterWriteAsync(responses);
        return ValueTask.CompletedTask;
    }

    private async Task EnqueueResponsesAfterWriteAsync(byte[][] responses)
    {
        // A physical radio cannot complete a response before the command write
        // has completed. Yielding here preserves that causal boundary in tests.
        await Task.Delay(1).ConfigureAwait(false);
        foreach (byte[] response in responses)
            await _responses.Writer.WriteAsync(response).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_response is null)
        {
            _response = await _responses.Reader.ReadAsync(
                ignoreReadCancellation ? CancellationToken.None : cancellationToken);
            _responseOffset = 0;
        }

        int count = Math.Min(buffer.Length, _response.Length - _responseOffset);
        _response.AsMemory(_responseOffset, count).CopyTo(buffer);
        _responseOffset += count;
        if (_responseOffset == _response.Length)
        {
            _response = null;
            _responseOffset = 0;
        }

        return count;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        IsConnected = false;
        _responses.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingWriteRadioTransport : IRadioTransport
{
    private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completeWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();

    public string Id => "blocking-write";
    public bool IsConnected { get; private set; }
    public Task WriteStarted => _writeStarted.Task;
    public string? WrittenFrame { get; private set; }

    public void CompleteWrite() => _completeWrite.TrySetResult();

    public ValueTask EmitAsync(string frame, CancellationToken cancellationToken = default) =>
        _responses.Writer.WriteAsync(Encoding.ASCII.GetBytes(frame), cancellationToken);

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        WrittenFrame = Encoding.ASCII.GetString(data.Span);
        _writeStarted.TrySetResult();
        await _completeWrite.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] response = await _responses.Reader.ReadAsync(cancellationToken);
        response.CopyTo(buffer);
        return response.Length;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _responses.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
