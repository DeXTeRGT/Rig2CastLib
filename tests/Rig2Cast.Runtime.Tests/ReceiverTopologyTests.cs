using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Runtime.Tests;

public sealed class ReceiverTopologyTests
{
    [Fact]
    public void ReceiverIdentifiersAreStableExtensibleValues()
    {
        Assert.Equal(ReceiverId.Main, new ReceiverId("MAIN"));
        Assert.Equal("sub", ReceiverId.Sub.ToString());
        Assert.Equal(new ReceiverId("receiver-3"), ReceiverId.Indexed(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("receiver/2")]
    public void InvalidReceiverIdentifiersAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => new ReceiverId(value));
    }
}
