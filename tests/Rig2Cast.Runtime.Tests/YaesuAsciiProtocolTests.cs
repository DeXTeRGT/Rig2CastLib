using System.Text;
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
        var protocol = new YaesuAsciiProtocol(transport);

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
        var protocol = new YaesuAsciiProtocol(transport);

        await Assert.ThrowsAsync<YaesuProtocolException>(() => protocol.QueryAsync("FA", "FA").AsTask());
    }
}

internal sealed class ScriptedRadioTransport : IRadioTransport
{
    private readonly Queue<(string Command, byte[]? Response)> _script = new();
    private byte[]? _response;
    private int _responseOffset;

    public string Id => "scripted";

    public bool IsConnected { get; private set; }

    public void Add(string command, string? response = null) =>
        _script.Enqueue((command, response is null ? null : Encoding.ASCII.GetBytes(response)));

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
        (string expected, byte[]? response) = _script.Dequeue();
        Assert.Equal(expected, Encoding.ASCII.GetString(data.Span));
        Assert.Null(_response);
        _response = response;
        _responseOffset = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.NotNull(_response);
        int count = Math.Min(buffer.Length, _response.Length - _responseOffset);
        _response.AsMemory(_responseOffset, count).CopyTo(buffer);
        _responseOffset += count;
        if (_responseOffset == _response.Length)
        {
            _response = null;
            _responseOffset = 0;
        }

        return ValueTask.FromResult(count);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
