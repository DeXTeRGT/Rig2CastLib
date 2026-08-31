using System.Text;
using System.Threading.Channels;
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.SendAsync("VS0").AsTask());
    }
}

internal sealed class ScriptedRadioTransport(bool ignoreReadCancellation = false) : IRadioTransport
{
    private readonly Queue<(string Command, byte[][] Responses)> _script = new();
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
    private byte[]? _response;
    private int _responseOffset;

    public string Id => "scripted";

    public bool IsConnected { get; private set; }

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
        Assert.NotEmpty(_script);
        (string expected, byte[][] responses) = _script.Dequeue();
        Assert.Equal(expected, Encoding.ASCII.GetString(data.Span));
        Assert.Null(_response);
        foreach (byte[] response in responses)
        {
            Assert.True(_responses.Writer.TryWrite(response));
        }
        return ValueTask.CompletedTask;
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
        IsConnected = false;
        _responses.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
