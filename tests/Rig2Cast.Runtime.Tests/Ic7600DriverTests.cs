using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Icom.Ic7600;
using Rig2Cast.Simulator;
using Rig2Cast.Simulator.Civ;

namespace Rig2Cast.Runtime.Tests;

public sealed class Ic7600DriverTests
{
    [Fact]
    public void FactoryAdvertisesDocumentedConnectionProfile()
    {
        RadioModelDescriptor model = Assert.Single(new Ic7600DriverFactory().Descriptor.Models);
        Assert.Equal(Ic7600Profile.ModelId, model.Id);
        Assert.Equal([300, 1_200, 4_800, 9_600, 19_200], model.SupportedBaudRates);
        Assert.Equal(19_200, model.DefaultBaudRate);
        Assert.Equal("7A", model.DefaultConnectionSettings!["icom.civAddress"]);
        Assert.Equal("E0", model.DefaultConnectionSettings["icom.controllerAddress"]);
    }

    [Fact]
    public async Task ReadOnlyStateReportsReceiverTopologyDualWatchAndSplit()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport, new CivSimulatorOptions
        {
            RadioAddress = 0x7A,
            StandardIdentity = 0x7A,
            SupportsIc7600ReceiverCommands = true,
            InitialFrequencyHz = 14_200_000,
            InitialBackgroundFrequencyHz = 7_100_000,
            InitialMode = 0x01,
            InitialBackgroundMode = 0x03,
            InitialActiveVfo = 0x01,
            InitialDualWatch = true,
            InitialSplit = true
        });
        await using IRadioDriver driver = await OpenAsync(transport);

        RadioState state = await driver.ReadStateAsync();
        DriverConformance.AssertCapabilities(driver);
        DriverConformance.AssertState(driver.Capabilities, state);
        Assert.Empty(driver.Capabilities.Vfos.Available);
        Assert.Equal(14_200_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Receivers[ReceiverId.Main].Mode);
        Assert.Equal(7_100_000, state.Receivers[ReceiverId.Sub].FrequencyHz);
        Assert.Equal(RadioMode.Cw, state.Receivers[ReceiverId.Sub].Mode);
        Assert.Equal(ReceiverId.Sub, state.SelectedReceiver);
        Assert.Equal(ReceiverId.Sub, state.TransmitReceiver);
        Assert.Equal(2, state.ReceivePaths.Count);
        Assert.Equal(new RadioSignalPath(ReceiverId.Sub, null), state.TransmitPath);
    }

    [Fact]
    public async Task InitialDriverRejectsMutationsAndExcludedModes()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport, new CivSimulatorOptions
        {
            RadioAddress = 0x7A,
            StandardIdentity = 0x7A,
            SupportsIc7600ReceiverCommands = true
        });
        await using IRadioDriver driver = await OpenAsync(transport);

        Assert.DoesNotContain(RadioMode.Rtty, driver.Capabilities.Modes.Values);
        Assert.DoesNotContain(RadioMode.RttyReverse, driver.Capabilities.Modes.Values);
        Assert.DoesNotContain(RadioMode.Psk, driver.Capabilities.Modes.Values);
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SetModeAsync(RadioMode.Rtty).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SetFrequencyAsync(VfoId.Current, 14_250_000).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SetSplitAsync(true).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SetPttAsync(true).AsTask());
    }

    [Fact]
    public async Task ConfigurableAddressStillRequiresFixedIc7600Identity()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport, new CivSimulatorOptions
        {
            RadioAddress = 0xA4,
            StandardIdentity = 0x7A,
            SupportsIc7600ReceiverCommands = true
        });
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["icom.civAddress"] = "A4"
        };
        await using IRadioDriver driver = await new Ic7600DriverFactory().OpenAsync(
            new RadioConnectionOptions("ic7600-test", Ic7600Profile.ModelId, settings), transport);

        Assert.Equal("A4", driver.Capabilities.Extensions["icom.civAddress"]);
        Assert.Equal(ConnectionStatus.Connected, (await driver.ReadStateAsync()).Connection);
    }

    private static async ValueTask<IRadioDriver> OpenAsync(InMemoryRadioTransport transport) =>
        await new Ic7600DriverFactory().OpenAsync(
            new RadioConnectionOptions("ic7600-test", Ic7600Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), transport);

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync()
    {
        var transport = new InMemoryRadioTransport("IC-7600 driver test");
        await transport.ConnectAsync();
        return transport;
    }
}
