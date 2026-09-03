using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Protocols.Civ;
using Rig2Cast.Simulator;
using Rig2Cast.Simulator.Civ;

namespace Rig2Cast.Runtime.Tests;

public sealed class Ic7300DriverTests
{
    [Fact]
    public void FactoryPublishesStableDocumentedConnectionMetadata()
    {
        var factory = new Ic7300DriverFactory();
        RadioModelDescriptor model = Assert.Single(factory.Descriptor.Models);

        Assert.Equal("rig2cast.drivers.icom.ic7300", factory.Descriptor.Id);
        Assert.Equal(Ic7300Profile.ModelId, model.Id);
        Assert.Equal("Icom", model.Manufacturer);
        Assert.Equal("IC-7300", model.Model);
        Assert.Equal(19_200, model.DefaultBaudRate);
        Assert.Equal([4_800, 9_600, 19_200, 38_400, 57_600, 115_200], model.SupportedBaudRates);
        Assert.Equal("94", model.DefaultConnectionSettings!["icom.civAddress"]);
    }

    [Fact]
    public async Task DriverReadsCompleteCurrentStateFromDocumentedCommands()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(
            transport,
            new CivSimulatorOptions
            {
                InitialFrequencyHz = 14_250_000,
                InitialMode = 0x03,
                InitialFilter = 0x02,
                InitialSplit = true,
                InitialTransmitting = true
            });
        await using IRadioDriver driver = await OpenAsync(transport);

        RadioState state = await driver.ReadStateAsync();

        Assert.Equal(14_250_000, state.FrequenciesHz[VfoId.Current]);
        Assert.Equal(VfoId.Current, state.ActiveVfo);
        Assert.Equal(RadioMode.Cw, state.Mode);
        Assert.True(state.IsSplit);
        Assert.True(state.IsTransmitting);
        Assert.Equal(ReceiverId.Main, state.SelectedReceiver);
        Assert.Equal(14_250_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(new RadioSignalPath(ReceiverId.Main, VfoId.Current), state.TransmitPath);
    }

    [Fact]
    public async Task CapabilitiesAdvertiseOnlyImplementedWrites()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        RadioCapabilities capabilities = driver.Capabilities;

        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, capabilities.Frequency.Feature.Access);
        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, capabilities.Modes.Feature.Access);
        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, capabilities.Vfos.Split.Access);
        Assert.Equal(CapabilitySupport.DriverNotImplemented, capabilities.Vfos.Selection.Support);
        Assert.Equal(CapabilitySupport.Supported, capabilities.Transmit.Support);
        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, capabilities.Transmit.Access);
        Assert.Equal(new HashSet<VfoId> { VfoId.Current }, capabilities.Frequency.Targets);
        Assert.Equal(11, capabilities.Modes.Values.Count);
        Assert.Contains(RadioMode.DataUsb, capabilities.Modes.Values);
        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, capabilities.Passband.Feature.Access);
        Assert.Equal(41, capabilities.Passband.ByMode[RadioMode.Usb].DiscreteValuesHz!.Count);
        Assert.Equal(50, capabilities.Passband.ByMode[RadioMode.Am].DiscreteValuesHz!.Count);
        Assert.DoesNotContain(RadioMode.Fm, capabilities.Passband.ByMode.Keys);
        Assert.Equal(7, capabilities.Controls.Count);
        Assert.Equal(6, capabilities.Switches.Count);
        Assert.Equal(3, capabilities.Choices.Count);
        Assert.Equal(4, capabilities.Meters.Count);

    }

    [Fact]
    public async Task SplitSetterRequiresAckAndVerifiesReadback()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);

        await driver.SetSplitAsync(true);
        Assert.True((await driver.ReadStateAsync()).IsSplit);

        await driver.SetSplitAsync(false);
        Assert.False((await driver.ReadStateAsync()).IsSplit);
    }

    [Fact]
    public async Task PttSetterRequiresAckAndStateReadReflectsMutation()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);

        await driver.SetPttAsync(true);
        Assert.True((await driver.ReadStateAsync()).IsTransmitting);

        await driver.SetPttAsync(false);
        Assert.False((await driver.ReadStateAsync()).IsTransmitting);
    }

    [Fact]
    public async Task PassbandUsesModeDependentDocumentedWidthTablesAndReadback()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var passband = Assert.IsAssignableFrom<IRadioPassbandDriver>(driver);
        var receiverPassband = Assert.IsAssignableFrom<IRadioReceiverPassbandDriver>(driver);

        Assert.Equal(2_400, (await passband.ReadPassbandAsync()).WidthHz);
        await passband.SetPassbandAsync(2_700);
        Assert.Equal(2_700, (await receiverPassband.ReadPassbandAsync(ReceiverId.Main)).WidthHz);

        await driver.SetModeAsync(RadioMode.Am);
        await receiverPassband.SetPassbandAsync(ReceiverId.Main, 6_000);
        Assert.Equal(6_000, (await passband.ReadPassbandAsync()).WidthHz);
        Assert.Equal(6_000, (await driver.ReadStateAsync()).Receivers[ReceiverId.Main].PassbandHz);

        await driver.SetModeAsync(RadioMode.Fm);
        await Assert.ThrowsAsync<NotSupportedException>(() => passband.ReadPassbandAsync().AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => passband.SetPassbandAsync(10_000).AsTask());
    }

    [Fact]
    public async Task PassbandRejectsWidthsOutsideTheCurrentModesDiscreteTable()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var passband = Assert.IsAssignableFrom<IRadioPassbandDriver>(driver);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => passband.SetPassbandAsync(550).AsTask());
        await driver.SetModeAsync(RadioMode.Am);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => passband.SetPassbandAsync(300).AsTask());
    }

    [Fact]
    public async Task NumericControlsChoicesSwitchesAndMetersUseDocumentedMappings()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);
        var choices = Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);
        var switches = Assert.IsAssignableFrom<IRadioSwitchDriver>(driver);
        var meters = Assert.IsAssignableFrom<IRadioMeterDriver>(driver);

        await controls.WriteControlAsync(RadioControlId.AfGain, 143);
        Assert.Equal(143, (await controls.ReadControlAsync(RadioControlId.AfGain)).Value);
        await controls.WriteControlAsync(RadioControlId.ClarifierOffsetHz, -1_250);
        Assert.Equal(-1_250, (await controls.ReadControlAsync(RadioControlId.ClarifierOffsetHz)).Value);

        await choices.WriteChoiceAsync(RadioChoiceId.Attenuator, "20db");
        await choices.WriteChoiceAsync(RadioChoiceId.Preamp, "preamp2");
        await choices.WriteChoiceAsync(RadioChoiceId.Agc, "slow");
        Assert.Equal("20db", (await choices.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
        Assert.Equal("preamp2", (await choices.ReadChoiceAsync(RadioChoiceId.Preamp)).Value);
        Assert.Equal("slow", (await choices.ReadChoiceAsync(RadioChoiceId.Agc)).Value);

        await switches.WriteSwitchAsync(RadioSwitchId.NoiseBlanker, true);
        await switches.WriteSwitchAsync(RadioSwitchId.ReceiveClarifier, true);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.NoiseBlanker)).Enabled);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.ReceiveClarifier)).Enabled);

        RadioMeterReading signal = await meters.ReadMeterAsync(RadioMeterId.SignalStrength);
        Assert.Equal(120, signal.RawValue);
        Assert.Equal(120 / 255d, signal.NormalizedValue, 12);
    }

    [Fact]
    public async Task DataModesCoordinateBaseModeAndDataFlag()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);

        await driver.SetModeAsync(RadioMode.DataUsb);
        Assert.Equal(RadioMode.DataUsb, (await driver.ReadStateAsync()).Mode);
        await driver.SetModeAsync(RadioMode.DataFm);
        Assert.Equal(RadioMode.DataFm, (await driver.ReadStateAsync()).Mode);
        await driver.SetModeAsync(RadioMode.Cw);
        Assert.Equal(RadioMode.Cw, (await driver.ReadStateAsync()).Mode);
    }

    [Fact]
    public async Task ReceiverControlAdaptersValidateMainReceiverAndRejectInvalidValues()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var controls = Assert.IsAssignableFrom<IRadioReceiverControlDriver>(driver);
        var choices = Assert.IsAssignableFrom<IRadioReceiverChoiceDriver>(driver);

        await controls.WriteControlAsync(RadioControlId.RfGain, ReceiverId.Main, 200);
        Assert.Equal(ReceiverId.Main,
            (await controls.ReadControlAsync(RadioControlId.RfGain, ReceiverId.Main)).Receiver);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => controls.WriteControlAsync(RadioControlId.RfGain, ReceiverId.Main, 256).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => controls.ReadControlAsync(RadioControlId.RfGain, ReceiverId.Sub).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => choices.WriteChoiceAsync(RadioChoiceId.Agc, ReceiverId.Main, "off").AsTask());
    }

    [Theory]
    [InlineData(RadioControlId.AfGain, 1)]
    [InlineData(RadioControlId.RfGain, 42)]
    [InlineData(RadioControlId.Squelch, 99)]
    [InlineData(RadioControlId.TransmitPower, 143)]
    [InlineData(RadioControlId.NoiseReductionLevel, 200)]
    [InlineData(RadioControlId.NoiseBlankerLevel, 255)]
    public async Task EveryAdvertisedLevelControlRoundTrips(RadioControlId control, int value)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);

        await controls.WriteControlAsync(control, value);

        Assert.Equal(value, (await controls.ReadControlAsync(control)).Value);
    }

    [Theory]
    [InlineData(RadioSwitchId.NoiseBlanker)]
    [InlineData(RadioSwitchId.NoiseReduction)]
    [InlineData(RadioSwitchId.AutoNotch)]
    [InlineData(RadioSwitchId.ManualNotch)]
    [InlineData(RadioSwitchId.ReceiveClarifier)]
    [InlineData(RadioSwitchId.TransmitClarifier)]
    public async Task EveryAdvertisedSwitchRoundTrips(RadioSwitchId control)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var switches = Assert.IsAssignableFrom<IRadioSwitchDriver>(driver);

        await switches.WriteSwitchAsync(control, true);

        Assert.True((await switches.ReadSwitchAsync(control)).Enabled);
    }

    [Theory]
    [InlineData(RadioMeterId.SignalStrength, 120)]
    [InlineData(RadioMeterId.Power, 0)]
    [InlineData(RadioMeterId.Swr, 0)]
    [InlineData(RadioMeterId.Alc, 0)]
    public async Task EveryAdvertisedMeterDecodesItsRawValue(RadioMeterId meter, int expected)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var meters = Assert.IsAssignableFrom<IRadioMeterDriver>(driver);

        RadioMeterReading reading = await meters.ReadMeterAsync(meter);

        Assert.Equal(expected, reading.RawValue);
        Assert.InRange(reading.NormalizedValue, 0, 1);
    }

    [Fact]
    public async Task FrequencyAndModeSettersRequireAckAndVerifyReadback()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);

        await driver.SetFrequencyAsync(VfoId.Current, 7_100_000);
        await driver.SetModeAsync(RadioMode.RttyReverse);
        RadioState state = await driver.ReadStateAsync();

        Assert.Equal(7_100_000, state.FrequenciesHz[VfoId.Current]);
        Assert.Equal(RadioMode.RttyReverse, state.Mode);

        var receiverFrequency = Assert.IsAssignableFrom<IRadioReceiverFrequencyDriver>(driver);
        var receiverMode = Assert.IsAssignableFrom<IRadioReceiverModeDriver>(driver);
        await receiverFrequency.SetFrequencyAsync(ReceiverId.Main, 14_074_000);
        await receiverMode.SetModeAsync(ReceiverId.Main, RadioMode.Usb);
        state = await driver.ReadStateAsync();
        Assert.Equal(14_074_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Receivers[ReceiverId.Main].Mode);
    }

    [Fact]
    public async Task SettersRejectInvalidTargetsValuesAndModesBeforeWriting()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.SetFrequencyAsync(VfoId.A, 7_100_000).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => driver.SetFrequencyAsync(VfoId.Current, 29_999).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.SetModeAsync(RadioMode.Psk).AsTask());

        var receiverFrequency = Assert.IsAssignableFrom<IRadioReceiverFrequencyDriver>(driver);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => receiverFrequency.SetFrequencyAsync(ReceiverId.Sub, 7_100_000).AsTask());
    }

    [Fact]
    public async Task FactoryUsesConfiguredHexAddress()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(
            transport, new CivSimulatorOptions { RadioAddress = 0xA4 });
        var factory = new Ic7300DriverFactory();
        await using IRadioDriver driver = await factory.OpenAsync(
            new RadioConnectionOptions(
                "icom-test",
                Ic7300Profile.ModelId,
                new Dictionary<string, string> { ["icom.civAddress"] = "0xA4" }),
            transport);

        Assert.Equal("A4", driver.Capabilities.Extensions["icom.civAddress"]);
        Assert.Equal(14_200_000, (await driver.ReadStateAsync()).FrequenciesHz[VfoId.Current]);
    }

    [Fact]
    public async Task TransceiveFramesBecomeTypedReceiverObservationsAtInjectedTime()
    {
        DateTimeOffset now = new(2032, 5, 6, 7, 8, 9, TimeSpan.Zero);
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport, new FixedTimeProvider(now));
        var source = Assert.IsAssignableFrom<IRadioObservationSource>(driver);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<RadioDriverObservation> observations = source
            .WatchObservationsAsync(timeout.Token).GetAsyncEnumerator();

        await simulator.EmitFrequencyTransceiveAsync(7_100_000);
        Assert.True(await observations.MoveNextAsync());
        var frequency = Assert.IsType<ReceiverFrequencyChangedObservation>(observations.Current);
        Assert.Equal(ReceiverId.Main, frequency.Receiver);
        Assert.Equal(7_100_000, frequency.FrequencyHz);
        Assert.Equal(now, frequency.ObservedAt);

        await simulator.EmitModeTransceiveAsync(0x08, 0x03);
        Assert.True(await observations.MoveNextAsync());
        var mode = Assert.IsType<ReceiverModeChangedObservation>(observations.Current);
        Assert.Equal(RadioMode.RttyReverse, mode.Mode);
        Assert.Equal(now, mode.ObservedAt);
    }

    [Fact]
    public async Task MalformedOrUnaddressedAnnouncementsRemainUnknown()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await OpenAsync(transport);
        var source = Assert.IsAssignableFrom<IRadioObservationSource>(driver);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<RadioDriverObservation> observations = source
            .WatchObservationsAsync(timeout.Token).GetAsyncEnumerator();

        await transport.SendRadioResponseAsync(CivFrameCodec.Encode(
            new CivFrame(0x00, 0x94, [0x01, 0x06, 0x01])));
        Assert.True(await observations.MoveNextAsync());
        Assert.IsType<UnknownFrameObservation>(observations.Current);
    }

    [Fact]
    public async Task FactoryRejectsUnknownModelAndInvalidAddress()
    {
        var factory = new Ic7300DriverFactory();
        await using var first = new InMemoryRadioTransport();
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.OpenAsync(
            new RadioConnectionOptions("test", "icom.unknown", new Dictionary<string, string>()), first).AsTask());

        await using var second = new InMemoryRadioTransport();
        await Assert.ThrowsAsync<ArgumentException>(() => factory.OpenAsync(
            new RadioConnectionOptions(
                "test", Ic7300Profile.ModelId,
                new Dictionary<string, string> { ["icom.civAddress"] = "not-hex" }), second).AsTask());
    }

    private static async ValueTask<IRadioDriver> OpenAsync(
        InMemoryRadioTransport transport,
        TimeProvider? timeProvider = null)
    {
        var factory = new Ic7300DriverFactory(timeProvider ?? TimeProvider.System);
        return await factory.OpenAsync(
            new RadioConnectionOptions(
                "icom-test", Ic7300Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            transport);
    }

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync()
    {
        var transport = new InMemoryRadioTransport("IC-7300 driver test");
        await transport.ConnectAsync();
        return transport;
    }
}
