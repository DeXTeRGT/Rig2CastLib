using System.Text;
using Rig2Cast.Rotators;

namespace Rig2Cast.Runtime.Tests;

public sealed class ProsistelRotatorDriverTests
{
    [Fact]
    public async Task InitializesWithCpmOffAndUsesVerifiedCommands()
    {
        var transport = new ScriptedRotatorTransport(
            Frame("A,?,015,B") + Frame("A,S,R") +
            Frame("A,?,031,B") + Frame("A,G,R") +
            Frame("A,G,R") + Frame("A,?,020,R"));
        var driver = new ProsistelRotatorDriver(transport);
        await using (driver)
        {
            await driver.InitializeAsync(CancellationToken.None);
            RotatorPosition movingPosition = await driver.ReadPositionAsync(CancellationToken.None);
            await driver.MoveToAsync(new RotatorPosition(20), CancellationToken.None);
            await driver.StopAsync(CancellationToken.None);
            RotatorPosition stoppedPosition = await driver.ReadPositionAsync(CancellationToken.None);

            Assert.Equal(31, movingPosition.Azimuth);
            Assert.Equal(20, stoppedPosition.Azimuth);
        }

        Assert.Equal(
            [Frame("AS"), Frame("A?"), Frame("AG20"), Frame("AG977"), Frame("A?")],
            transport.Commands);
    }

    [Fact]
    public async Task ReportsControllerErrors()
    {
        var transport = new ScriptedRotatorTransport(Frame("A,G,E,00006"));
        await using var driver = new ProsistelRotatorDriver(transport);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.MoveToAsync(new RotatorPosition(20), CancellationToken.None).AsTask());

        Assert.Contains("00006", error.Message);
    }

    [Fact]
    public async Task RejectsUnsupportedAndOutOfRangeMovement()
    {
        await using var driver = new ProsistelRotatorDriver(new ScriptedRotatorTransport(""), 360);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            driver.MoveToAsync(new RotatorPosition(361), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            driver.StartAsync(RotatorDirection.Clockwise, CancellationToken.None).AsTask());
    }

    private static string Frame(string payload) => $"\x02{payload}\r";

    private sealed class ScriptedRotatorTransport(string responses) : IRotatorTransport
    {
        private readonly Queue<byte> _responses = new(Encoding.ASCII.GetBytes(responses));
        public List<string> Commands { get; } = [];
        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Commands.Add(Encoding.ASCII.GetString(data.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = _responses.Dequeue();
            return ValueTask.FromResult(1);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
