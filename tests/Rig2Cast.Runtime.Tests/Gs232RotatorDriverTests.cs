using System.Text;
using Rig2Cast.Rotators;

namespace Rig2Cast.Runtime.Tests;

public sealed class Gs232RotatorDriverTests
{
    [Fact]
    public async Task ReadsAzimuthAndUsesGs232Commands()
    {
        var transport = new ScriptedRotatorTransport("+0123\r");
        await using var driver = new Gs232RotatorDriver(transport, 450);

        RotatorPosition position = await driver.ReadPositionAsync(CancellationToken.None);
        await driver.MoveToAsync(new RotatorPosition(275), CancellationToken.None);
        await driver.StartAsync(RotatorDirection.Clockwise, CancellationToken.None);
        await driver.StartAsync(RotatorDirection.Counterclockwise, CancellationToken.None);
        await driver.StopAsync(CancellationToken.None);

        Assert.Equal(123, position.Azimuth);
        Assert.Equal(["C\r", "M275\r", "R\r", "L\r", "S\r"], transport.Commands);
    }

    [Fact]
    public async Task RejectsAzimuthOutsideAdvertisedRange()
    {
        await using var driver = new Gs232RotatorDriver(new ScriptedRotatorTransport(""), 450);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            driver.MoveToAsync(new RotatorPosition(451), CancellationToken.None).AsTask());
    }

    private sealed class ScriptedRotatorTransport(string response) : IRotatorTransport
    {
        private readonly Queue<byte> _response = new(Encoding.ASCII.GetBytes(response));
        public List<string> Commands { get; } = [];
        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Commands.Add(Encoding.ASCII.GetString(data.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_response.Count == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = _response.Dequeue();
            return ValueTask.FromResult(1);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
