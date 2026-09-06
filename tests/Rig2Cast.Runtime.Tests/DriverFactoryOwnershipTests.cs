using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Drivers.Xiegu.G90;
using Rig2Cast.Drivers.Yaesu.Ftdx10;

namespace Rig2Cast.Runtime.Tests;

public sealed class DriverFactoryOwnershipTests
{
    [Fact]
    public async Task BuiltInFactoriesDisposeTransportWhenModelIsUnsupported()
    {
        IRadioDriverFactory[] factories =
        [
            new Ftdx10DriverFactory(),
            new ElecraftK3DriverFactory(),
            new Ic7300DriverFactory(),
            new G90DriverFactory()
        ];

        foreach (IRadioDriverFactory factory in factories)
        {
            var transport = new ScriptedRadioTransport();
            var options = new RadioConnectionOptions(
                "ownership-test", "rig2cast.unsupported-model", new Dictionary<string, string>());

            await Assert.ThrowsAsync<NotSupportedException>(
                () => factory.OpenAsync(options, transport).AsTask());

            Assert.Equal(1, transport.DisposeCount);
        }
    }
}
