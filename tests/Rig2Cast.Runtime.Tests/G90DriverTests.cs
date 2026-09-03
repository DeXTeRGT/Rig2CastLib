using System.Threading.Channels;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Xiegu.G90;
using Rig2Cast.Protocols.Civ;
using Rig2Cast.Simulator;
using Rig2Cast.Simulator.Civ;

namespace Rig2Cast.Runtime.Tests;

public sealed class G90DriverTests
{
    [Fact]
    public void FactoryAdvertisesG90SerialDefaults()
    {
        RadioModelDescriptor model = Assert.Single(new G90DriverFactory().Descriptor.Models);
        Assert.Equal(G90Profile.ModelId, model.Id);
        Assert.Equal("Xiegu", model.Manufacturer);
        Assert.Equal("G90", model.Model);
        Assert.Equal(19_200, model.DefaultBaudRate);
        Assert.Equal([19_200], model.SupportedBaudRates);
        Assert.Equal("70", model.DefaultConnectionSettings!["icom.civAddress"]);
    }

    [Fact]
    public async Task CoreStateAndMutationsRoundTripOverCiv()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);

        RadioState initial = await driver.ReadStateAsync();
        Assert.Equal(14_200_000, initial.FrequenciesHz[VfoId.Current]);
        Assert.Equal(RadioMode.Usb, initial.Mode);

        await driver.SetFrequencyAsync(VfoId.Current, 7_074_000);
        await driver.SetModeAsync(RadioMode.CwReverse);
        await driver.SetSplitAsync(true);
        await driver.SetPttAsync(true);

        RadioState changed = await driver.ReadStateAsync();
        Assert.Equal(7_074_000, changed.FrequenciesHz[VfoId.Current]);
        Assert.Equal(RadioMode.CwReverse, changed.Mode);
        Assert.True(changed.IsSplit);
        Assert.True(changed.IsTransmitting);
    }

    [Fact]
    public async Task FallsBackToFrequencyProbeWhenOlderFirmwareRejectsXieguIdentity()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions
            {
                RadioAddress = 0x70,
                SupportsXieguIdentity = false
            });

        await using IRadioDriver driver = await OpenAsync(transport);

        Assert.Equal(14_200_000, (await driver.ReadStateAsync()).FrequenciesHz[VfoId.Current]);
        Assert.Equal(false, driver.Capabilities.Extensions["xiegu.identityVerified"]);
    }

    [Fact]
    public async Task ReplacesTimedOutSessionBeforeOlderFirmwareFrequencyFallback()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        simulator.SetNextResponse(CivSimulatorNextResponse.Drop);

        await using G90Driver driver = await G90Driver.OpenAsync(
            transport, responseTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(14_200_000, (await driver.ReadStateAsync()).FrequenciesHz[VfoId.Current]);
        Assert.Equal(false, driver.Capabilities.Extensions["xiegu.identityVerified"]);
    }

    [Fact]
    public async Task OptionalProbeTimeoutResetsTransportBeforeAwaitingCancellationIgnoringReader()
    {
        await using var transport = new CancellationIgnoringG90Transport();

        await using G90Driver driver = await G90Driver.OpenAsync(
            transport, responseTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(2, transport.ConnectionCount);
        Assert.Equal(false, driver.Capabilities.Extensions["xiegu.extendedVfoSupported"]);
        Assert.Equal(14_200_000, (await driver.ReadStateAsync()).FrequenciesHz[VfoId.Current]);
    }

    [Fact]
    public async Task CapabilitiesAreConservativeAndMatchImplementedSurface()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);

        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, driver.Capabilities.Frequency.Feature.Access);
        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, driver.Capabilities.Modes.Feature.Access);
        Assert.Equal(CapabilitySupport.DriverNotImplemented, driver.Capabilities.Vfos.Selection.Support);
        Assert.Equal(FeatureAccess.Read | FeatureAccess.Write, driver.Capabilities.Transmit.Access);
        Assert.Equal(6, driver.Capabilities.Modes.Values.Count);
        Assert.Equal(9, driver.Capabilities.Controls.Count);
        Assert.All(driver.Capabilities.Controls.Where(item => item.Key != RadioControlId.ClarifierOffsetHz),
            item => Assert.Equal(FeatureAccess.Read, item.Value.Feature.Access));
        Assert.Equal(FeatureAccess.Read, driver.Capabilities.Controls[RadioControlId.NoiseReductionLevel].Feature.Access);
        Assert.Equal(FeatureAccess.Read, driver.Capabilities.Controls[RadioControlId.MonitorLevel].Feature.Access);
        Assert.Equal(6, driver.Capabilities.Switches.Count);
        Assert.Equal(FeatureAccess.Read, driver.Capabilities.Switches[RadioSwitchId.DialLock].Feature.Access);
        Assert.Equal(3, driver.Capabilities.Choices.Count);
        Assert.Equal(4, driver.Capabilities.Meters.Count);
        Assert.Equal("documented-simulated-partially-hardware-validated", driver.Capabilities.Extensions["rig2cast.validation"]);
        Assert.Equal("0090", driver.Capabilities.Extensions["xiegu.identity"]);
        Assert.Equal(false, driver.Capabilities.Extensions["xiegu.extendedVfoSupported"]);
    }

    [Fact]
    public async Task StateTimestampUsesInjectedTimeProvider()
    {
        DateTimeOffset now = new(2035, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70 });
        await using IRadioDriver driver = await OpenAsync(transport, new FixedTimeProvider(now));

        Assert.Equal(now, (await driver.ReadStateAsync()).ObservedAt);
    }

    [Fact]
    public async Task XieguSpecificIdentificationReturnsG90FamilyCode()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using var session = new CivSession(transport);

        CivFrame response = await session.QueryAsync(
            new CivFrame(0x70, 0xE0, [0x1D, 0x19]), new byte[] { 0x1D, 0x19 });

        Assert.Equal(new byte[] { 0x1D, 0x19, 0x00, 0x90 }, response.Message.ToArray());
    }

    [Theory]
    [InlineData(RadioControlId.AfGain, 143)]
    [InlineData(RadioControlId.TransmitPower, 77)]
    [InlineData(RadioControlId.MicrophoneGain, 201)]
    [InlineData(RadioControlId.NoiseBlankerLevel, 42)]
    [InlineData(RadioControlId.AntiVoxLevel, 255)]
    public async Task HardwareBinaryLevelsDecodeAndRemainReadOnlyUntilWriteScalingIsKnown(
        RadioControlId control, int value)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);
        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);

        Assert.InRange((await controls.ReadControlAsync(control)).Value, 0, 607);
        await Assert.ThrowsAsync<NotSupportedException>(() => controls.WriteControlAsync(control, value).AsTask());
    }

    [Theory]
    [InlineData(RadioControlId.RfGain)]
    [InlineData(RadioControlId.NoiseReductionLevel)]
    [InlineData(RadioControlId.MonitorLevel)]
    public async Task AsymmetricG90LevelsRemainReadOnly(RadioControlId control)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);
        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);

        Assert.InRange((await controls.ReadControlAsync(control)).Value, 0, 255);
        await Assert.ThrowsAsync<NotSupportedException>(() => controls.WriteControlAsync(control, 1).AsTask());
    }

    [Fact]
    public async Task ChoicesSwitchesAndRitRoundTripWhileDialLockRemainsReadOnly()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);
        var choices = Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);
        var switches = Assert.IsAssignableFrom<IRadioSwitchDriver>(driver);
        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);

        await choices.WriteChoiceAsync(RadioChoiceId.Preamp, "on");
        await choices.WriteChoiceAsync(RadioChoiceId.Agc, "off");
        await choices.WriteChoiceAsync(RadioChoiceId.Attenuator, "on");
        await switches.WriteSwitchAsync(RadioSwitchId.NoiseBlanker, true);
        await switches.WriteSwitchAsync(RadioSwitchId.SpeechProcessor, true);
        await switches.WriteSwitchAsync(RadioSwitchId.AntennaTuner, true);
        await switches.WriteSwitchAsync(RadioSwitchId.ReceiveClarifier, true);
        await switches.WriteSwitchAsync(RadioSwitchId.TransmitClarifier, true);
        await controls.WriteControlAsync(RadioControlId.ClarifierOffsetHz, -1_250);

        Assert.Equal("on", (await choices.ReadChoiceAsync(RadioChoiceId.Preamp)).Value);
        Assert.Equal("off", (await choices.ReadChoiceAsync(RadioChoiceId.Agc)).Value);
        Assert.Equal("on", (await choices.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.NoiseBlanker)).Enabled);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.SpeechProcessor)).Enabled);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.AntennaTuner)).Enabled);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.ReceiveClarifier)).Enabled);
        Assert.True((await switches.ReadSwitchAsync(RadioSwitchId.TransmitClarifier)).Enabled);
        Assert.Equal(-1_250, (await controls.ReadControlAsync(RadioControlId.ClarifierOffsetHz)).Value);
        Assert.False((await switches.ReadSwitchAsync(RadioSwitchId.DialLock)).Enabled);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => switches.WriteSwitchAsync(RadioSwitchId.DialLock, true).AsTask());
    }

    [Fact]
    public async Task AttenuatorToggleUsesReadbackWhenRadioReturnsNoAcknowledgement()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);
        var choices = Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);

        await choices.WriteChoiceAsync(RadioChoiceId.Attenuator, "on");
        Assert.Equal("on", (await choices.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);

        await choices.WriteChoiceAsync(RadioChoiceId.Attenuator, "off");
        Assert.Equal("off", (await choices.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("fast")]
    [InlineData("slow")]
    [InlineData("auto")]
    public async Task AgcChoiceUsesPhysicallyObservedG90Names(string value)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);
        var choices = Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);

        await choices.WriteChoiceAsync(RadioChoiceId.Agc, value);

        Assert.Equal(value, (await choices.ReadChoiceAsync(RadioChoiceId.Agc)).Value);
    }

    [Fact]
    public async Task ExtendedVfoStateSelectionFrequencyAndSplitRoundTrip()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions
            {
                RadioAddress = 0x70,
                SupportsXieguIdentity = true,
                SupportsXieguExtendedVfo = true,
                InitialFrequencyHz = 14_319_670,
                InitialBackgroundFrequencyHz = 7_100_000
            });
        await using IRadioDriver driver = await OpenAsync(transport);

        RadioState initial = await driver.ReadStateAsync();
        Assert.Equal(VfoId.A, initial.ActiveVfo);
        Assert.Equal(14_319_670, initial.FrequenciesHz[VfoId.A]);
        Assert.Equal(7_100_000, initial.FrequenciesHz[VfoId.B]);
        Assert.Equal(true, driver.Capabilities.Extensions["xiegu.extendedVfoSupported"]);

        await driver.SetFrequencyAsync(VfoId.B, 7_060_000);
        await driver.SetActiveVfoAsync(VfoId.B);
        await driver.SetSplitAsync(true);

        RadioState changed = await driver.ReadStateAsync();
        Assert.Equal(VfoId.B, changed.ActiveVfo);
        Assert.Equal(14_319_670, changed.FrequenciesHz[VfoId.A]);
        Assert.Equal(7_060_000, changed.FrequenciesHz[VfoId.B]);
        Assert.Equal(VfoId.A, changed.TransmitVfo);
        Assert.Equal(new RadioSignalPath(ReceiverId.Main, VfoId.B), Assert.Single(changed.ReceivePaths));
        Assert.Equal(new RadioSignalPath(ReceiverId.Main, VfoId.A), changed.TransmitPath);
    }

    [Fact]
    public async Task ExtendedVfoDataModeRoundTripsAndPreservesFilter()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions
            {
                RadioAddress = 0x70,
                SupportsXieguIdentity = true,
                SupportsXieguExtendedVfo = true,
                InitialFilter = 0x02
            });
        await using IRadioDriver driver = await OpenAsync(transport);

        Assert.Contains(RadioMode.DataUsb, driver.Capabilities.Modes.Values);
        Assert.Contains(RadioMode.DataLsb, driver.Capabilities.Modes.Values);

        await driver.SetModeAsync(RadioMode.DataUsb);
        RadioState data = await driver.ReadStateAsync();
        Assert.Equal(RadioMode.DataUsb, data.Mode);
        Assert.Equal(RadioMode.DataUsb, data.Vfos[VfoId.A].Mode);

        await driver.SetModeAsync(RadioMode.Usb);
        Assert.Equal(RadioMode.Usb, (await driver.ReadStateAsync()).Mode);
    }

    [Theory]
    [InlineData(RadioMeterId.SignalStrength, 120)]
    [InlineData(RadioMeterId.Power, 0)]
    [InlineData(RadioMeterId.Swr, 0)]
    [InlineData(RadioMeterId.Alc, 0)]
    public async Task DocumentedMetersExposeRawUncalibratedValues(RadioMeterId meter, int expected)
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await OpenAsync(transport);
        var meters = Assert.IsAssignableFrom<IRadioMeterDriver>(driver);

        RadioMeterReading reading = await meters.ReadMeterAsync(meter);

        Assert.Equal(expected, reading.RawValue);
        Assert.InRange(reading.NormalizedValue, 0, 1);
    }

    [Fact]
    public async Task RejectsUnsupportedTargetsModesAndOutOfRangeFrequencyBeforeMutation()
    {
        await using var transport = await ConnectedTransportAsync();
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70 });
        await using IRadioDriver driver = await OpenAsync(transport);

        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SetFrequencyAsync(VfoId.A, 7_100_000).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.SetFrequencyAsync(VfoId.Current, 499_999).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.SetModeAsync(RadioMode.DataUsb).AsTask());
    }

    private static async ValueTask<IRadioDriver> OpenAsync(InMemoryRadioTransport transport, TimeProvider? timeProvider = null) =>
        await new G90DriverFactory(timeProvider ?? TimeProvider.System).OpenAsync(
            new RadioConnectionOptions("g90-test", G90Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), transport);

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync()
    {
        var transport = new InMemoryRadioTransport("G90 driver test");
        await transport.ConnectAsync();
        return transport;
    }

    private sealed class CancellationIgnoringG90Transport : IRadioTransport
    {
        private Channel<byte[]>? _responses;
        private byte[]? _pending;
        private int _pendingOffset;

        public string Id => "cancellation-ignoring-g90";
        public bool IsConnected { get; private set; }
        public int ConnectionCount { get; private set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _responses = Channel.CreateUnbounded<byte[]>();
            _pending = null;
            _pendingOffset = 0;
            IsConnected = true;
            ConnectionCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            _responses?.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] request = data.ToArray();
            ReadOnlySpan<byte> message = request.AsSpan(4, request.Length - 5);
            byte[]? response = message switch
            {
                [0x1D, 0x19] => Frame(0x1D, 0x19, 0x00, 0x90),
                [0x25, 0x00] when ConnectionCount == 1 => null,
                [0x03] => Frame(0x03, 0x00, 0x00, 0x20, 0x14, 0x00),
                [0x04] => Frame(0x04, 0x01, 0x01),
                [0x0F] => Frame(0x0F, 0x00),
                [0x1C, 0x00] => Frame(0x1C, 0x00, 0x00),
                _ => Frame(0xFA)
            };
            return response is null
                ? ValueTask.CompletedTask
                : _responses!.Writer.WriteAsync(response, cancellationToken);
        }

        public async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Deliberately ignore cancellation, matching affected Windows serial drivers.
            if (_pending is null)
            {
                _pending = await _responses!.Reader.ReadAsync(CancellationToken.None);
                _pendingOffset = 0;
            }
            int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
            _pendingOffset += count;
            if (_pendingOffset == _pending.Length)
                _pending = null;
            return count;
        }

        public ValueTask DisposeAsync() => DisconnectAsync();

        private static byte[] Frame(params byte[] message) =>
            [0xFE, 0xFE, 0xE0, 0x70, .. message, 0xFD];
    }
}
