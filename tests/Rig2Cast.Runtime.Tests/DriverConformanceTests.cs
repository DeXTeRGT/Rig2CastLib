using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Drivers.Xiegu.G90;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Simulator;
using Rig2Cast.Simulator.Civ;

namespace Rig2Cast.Runtime.Tests;

public sealed class DriverConformanceTests
{
    [Fact]
    public async Task Ftdx10CapabilitiesAndStateConformToCommonContracts()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("IF;", "IF001014250000+000000200000;");
        transport.Add("OI;", "OI001007100000+000000300000;");
        transport.Add("VS;", "VS0;");
        transport.Add("ST;", "ST1;");
        transport.Add("TX;", "TX0;");
        transport.Add("FA014225000;");
        transport.Add("MD03;");
        transport.Add("ST0;");
        transport.Add("AG0200;");
        transport.Add("NB00;");
        transport.Add("RA01;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        DriverConformance.AssertCapabilities(driver);
        RadioState state = await driver.ReadStateAsync();
        DriverConformance.AssertState(driver.Capabilities, state);
        await DriverConformance.AssertCoreMutationsAsync(
            driver, state, new(VfoId.A, 14_225_000, RadioMode.Cw, false), verifyRoundTrip: false);
        await DriverConformance.AssertFeatureMutationsAsync(
            driver,
            new(new(RadioControlId.AfGain, 200), new(RadioSwitchId.NoiseBlanker, false),
                new(RadioChoiceId.Attenuator, "6db")),
            RadioMode.Cw,
            verifyReadback: false);
        await DriverConformance.AssertRejectsInvalidCoreMutationsAsync(driver);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ElecraftK3sCapabilitiesAndStateConformToCommonContracts()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S----VR--;");
        transport.Add("FA;", "FA00014250000;");
        transport.Add("FB;", "FB00014275000;");
        transport.Add("IF;", "IF00014250000     +000000 0002001001 ;");
        transport.Add("FT;", "FT1;");
        transport.Add("TQ;", "TQ0;");
        transport.Add("FA00014060000;");
        transport.Add("IF;", "IF00014060000     +000000 0002001001 ;");
        transport.Add("MD3;");
        transport.Add("FR0;");
        transport.Add("AG128;");
        transport.Add("RT1;");
        transport.Add("RA05;");
        transport.Add("BW0270;");
        transport.Add("IF;", "IF00014060000     +000000 0002001001 ;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        DriverConformance.AssertCapabilities(driver);
        RadioState state = await driver.ReadStateAsync();
        DriverConformance.AssertState(driver.Capabilities, state);
        await DriverConformance.AssertCoreMutationsAsync(
            driver, state, new(VfoId.A, 14_060_000, RadioMode.Cw, false), verifyRoundTrip: false);
        await DriverConformance.AssertFeatureMutationsAsync(
            driver,
            new(new(RadioControlId.AfGain, 128), new(RadioSwitchId.ReceiveClarifier, true),
                new(RadioChoiceId.Attenuator, "5db"), 2_700),
            RadioMode.Cw,
            verifyReadback: false);
        await DriverConformance.AssertRejectsInvalidCoreMutationsAsync(driver);
        transport.AssertComplete();
    }

    [Fact]
    public async Task Ic7300CapabilitiesAndStateConformToCommonContracts()
    {
        await using var transport = await ConnectedTransportAsync("IC-7300 conformance");
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await new Ic7300DriverFactory().OpenAsync(
            new RadioConnectionOptions("icom-conformance", Ic7300Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), transport);

        DriverConformance.AssertCapabilities(driver);
        RadioState state = await driver.ReadStateAsync();
        DriverConformance.AssertState(driver.Capabilities, state);
        await DriverConformance.AssertCoreMutationsAsync(
            driver, state, new(VfoId.Current, 7_100_000, RadioMode.RttyReverse, true));
        await DriverConformance.AssertFeatureMutationsAsync(
            driver,
            new(new(RadioControlId.AfGain, 143), new(RadioSwitchId.NoiseBlanker, true),
                new(RadioChoiceId.Attenuator, "20db"), 2_700),
            RadioMode.RttyReverse);
        await DriverConformance.AssertRejectsInvalidCoreMutationsAsync(driver);
    }

    [Fact]
    public async Task G90CapabilitiesAndStateConformToCommonContracts()
    {
        await using var transport = await ConnectedTransportAsync("G90 conformance");
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await new G90DriverFactory().OpenAsync(
            new RadioConnectionOptions("g90-conformance", G90Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), transport);

        DriverConformance.AssertCapabilities(driver);
        RadioState state = await driver.ReadStateAsync();
        DriverConformance.AssertState(driver.Capabilities, state);
        await DriverConformance.AssertCoreMutationsAsync(
            driver, state, new(VfoId.Current, 7_074_000, RadioMode.CwReverse, true));
        await DriverConformance.AssertFeatureMutationsAsync(
            driver,
            new(new(RadioControlId.ClarifierOffsetHz, -1_250),
                new(RadioSwitchId.NoiseBlanker, true), new(RadioChoiceId.Preamp, "on")),
            RadioMode.CwReverse);
        await DriverConformance.AssertRejectsInvalidCoreMutationsAsync(driver);
    }

    [Fact]
    public async Task Ftdx10RepresentativeReadsConformToDescriptors()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("AG0;", "AG0128;");
        transport.Add("NB0;", "NB01;");
        transport.Add("RA0;", "RA03;");
        transport.Add("SM0;", "SM0123;");
        transport.Add("MD0;", "MD02;");
        transport.Add("SH0;", "SH0013;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        await DriverConformance.AssertFeatureReadsAsync(driver,
            new(RadioControlId.AfGain, RadioSwitchId.NoiseBlanker, RadioChoiceId.Attenuator,
                RadioMode.Usb, RadioMeterId.SignalStrength, IncludePassband: true));
        transport.AssertComplete();
    }

    [Fact]
    public async Task ElecraftK3sRepresentativeReadsConformToDescriptors()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("AG;", "AG123;");
        transport.Add("RT;", "RT1;");
        transport.Add("RA;", "RA05;");
        transport.Add("SMH;", "SMH040;");
        transport.Add("BW;", "BW0240;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        await DriverConformance.AssertFeatureReadsAsync(driver,
            new(RadioControlId.AfGain, RadioSwitchId.ReceiveClarifier, RadioChoiceId.Attenuator,
                RadioMode.Usb, RadioMeterId.SignalStrength, IncludePassband: true));
        transport.AssertComplete();
    }

    [Fact]
    public async Task Ic7300RepresentativeReadsConformToDescriptors()
    {
        await using var transport = await ConnectedTransportAsync("IC-7300 read conformance");
        await using var simulator = new CivRadioSimulator(transport);
        await using IRadioDriver driver = await new Ic7300DriverFactory().OpenAsync(
            new RadioConnectionOptions("icom-read-conformance", Ic7300Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), transport);

        await DriverConformance.AssertFeatureReadsAsync(driver,
            new(RadioControlId.AfGain, RadioSwitchId.NoiseBlanker, RadioChoiceId.Attenuator,
                RadioMode.Usb, RadioMeterId.SignalStrength, IncludePassband: true));
    }

    [Fact]
    public async Task G90RepresentativeReadsConformToDescriptors()
    {
        await using var transport = await ConnectedTransportAsync("G90 read conformance");
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        await using IRadioDriver driver = await new G90DriverFactory().OpenAsync(
            new RadioConnectionOptions("g90-read-conformance", G90Profile.ModelId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), transport);

        await DriverConformance.AssertFeatureReadsAsync(driver,
            new(RadioControlId.ClarifierOffsetHz, RadioSwitchId.NoiseBlanker, RadioChoiceId.Preamp,
                RadioMode.Usb, RadioMeterId.SignalStrength));
    }

    [Fact]
    public async Task Ftdx10FactoryConformsToTransportOwnershipContract()
    {
        var factory = new Ftdx10DriverFactory();
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        IRadioDriver driver = await factory.OpenAsync(Options(Ftdx10CatProfile.ModelId), transport);

        await DriverConformance.AssertDriverOwnsTransportAsync(driver, transport);
        Assert.Equal(1, transport.DisposeCount);
        var failedTransport = new ScriptedRadioTransport();
        await failedTransport.ConnectAsync();
        await DriverConformance.AssertUnknownModelDisposesTransportAsync(factory, failedTransport);
        Assert.Equal(1, failedTransport.DisposeCount);
    }

    [Fact]
    public async Task ElecraftFactoryConformsToTransportOwnershipContract()
    {
        var factory = new ElecraftK3DriverFactory();
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S----VR--;");
        IRadioDriver driver = await factory.OpenAsync(Options(ElecraftK3Profile.K3SModelId), transport);

        await DriverConformance.AssertDriverOwnsTransportAsync(driver, transport);
        Assert.Equal(1, transport.DisposeCount);
        var failedTransport = new ScriptedRadioTransport();
        await failedTransport.ConnectAsync();
        await DriverConformance.AssertUnknownModelDisposesTransportAsync(factory, failedTransport);
        Assert.Equal(1, failedTransport.DisposeCount);
    }

    [Fact]
    public async Task Ic7300FactoryConformsToTransportOwnershipContract()
    {
        var factory = new Ic7300DriverFactory();
        var transport = await ConnectedTransportAsync("IC-7300 ownership conformance");
        await using var simulator = new CivRadioSimulator(transport);
        IRadioDriver driver = await factory.OpenAsync(Options(Ic7300Profile.ModelId), transport);

        await DriverConformance.AssertDriverOwnsTransportAsync(driver, transport);
        await DriverConformance.AssertUnknownModelDisposesTransportAsync(
            factory, await ConnectedTransportAsync("IC-7300 failed-open conformance"));
    }

    [Fact]
    public async Task G90FactoryConformsToTransportOwnershipContract()
    {
        var factory = new G90DriverFactory();
        var transport = await ConnectedTransportAsync("G90 ownership conformance");
        await using var simulator = new CivRadioSimulator(transport,
            new CivSimulatorOptions { RadioAddress = 0x70, SupportsXieguIdentity = true });
        IRadioDriver driver = await factory.OpenAsync(Options(G90Profile.ModelId), transport);

        await DriverConformance.AssertDriverOwnsTransportAsync(driver, transport);
        await DriverConformance.AssertUnknownModelDisposesTransportAsync(
            factory, await ConnectedTransportAsync("G90 failed-open conformance"));
    }

    private static RadioConnectionOptions Options(string modelId) =>
        new("conformance", modelId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync(string name)
    {
        var transport = new InMemoryRadioTransport(name);
        await transport.ConnectAsync();
        return transport;
    }
}

internal static class DriverConformance
{
    internal sealed record CoreMutationScenario(
        VfoId FrequencyTarget,
        long FrequencyHz,
        RadioMode Mode,
        bool SplitEnabled);

    internal sealed record NumericMutation(RadioControlId Id, int Value);

    internal sealed record SwitchMutation(RadioSwitchId Id, bool Enabled);

    internal sealed record ChoiceMutation(RadioChoiceId Id, string Value);

    internal sealed record FeatureMutationScenario(
        NumericMutation Numeric,
        SwitchMutation Switch,
        ChoiceMutation Choice,
        int? PassbandHz = null);

    internal sealed record FeatureReadScenario(
        RadioControlId Numeric,
        RadioSwitchId Switch,
        RadioChoiceId Choice,
        RadioMode ActiveMode,
        RadioMeterId Meter,
        bool IncludePassband = false);

    public static void AssertCapabilities(IRadioDriver driver)
    {
        RadioCapabilities capabilities = driver.Capabilities;
        Assert.False(string.IsNullOrWhiteSpace(capabilities.DriverId));
        Assert.NotEmpty(capabilities.Vfos.Available);
        Assert.NotEmpty(capabilities.Receivers.Available);
        Assert.NotEmpty(capabilities.Modes.Values);
        AssertFeature(capabilities.Frequency.Feature);
        AssertFeature(capabilities.Modes.Feature);
        AssertFeature(capabilities.Vfos.Selection);
        AssertFeature(capabilities.Vfos.Split);
        AssertFeature(capabilities.Transmit);
        AssertFeature(capabilities.Passband.Feature);

        AssertSubset(capabilities.Frequency.Targets, capabilities.Vfos.Available);
        AssertReceiverTargets(capabilities, capabilities.Frequency.ReceiverTargets);
        AssertReceiverTargets(capabilities, capabilities.Modes.ReceiverTargets);
        AssertReceiverTargets(capabilities, capabilities.Passband.ReceiverTargets);
        AssertSubset(capabilities.Passband.Targets, capabilities.Vfos.Available);

        if (capabilities.Frequency.ReceiverTargets.Count > 0)
            Assert.IsAssignableFrom<IRadioReceiverFrequencyDriver>(driver);
        if (capabilities.Modes.ReceiverTargets.Count > 0)
            Assert.IsAssignableFrom<IRadioReceiverModeDriver>(driver);
        if (IsAvailable(capabilities.Passband.Feature))
            Assert.IsAssignableFrom<IRadioPassbandDriver>(driver);
        if (capabilities.Passband.Targets.Count > 0)
            Assert.IsAssignableFrom<IRadioTargetedPassbandDriver>(driver);
        if (capabilities.Passband.ReceiverTargets.Count > 0)
            Assert.IsAssignableFrom<IRadioReceiverPassbandDriver>(driver);

        foreach (FrequencyRange range in capabilities.Frequency.Ranges)
            Assert.True(range.MinimumHz <= range.MaximumHz);
        if (capabilities.Frequency.RangesByReceiver is { } rangesByReceiver)
        {
            AssertReceiverTargets(capabilities, rangesByReceiver.Keys);
            foreach (FrequencyRange range in rangesByReceiver.Values.SelectMany(ranges => ranges))
                Assert.True(range.MinimumHz <= range.MaximumHz);
        }

        if (capabilities.Modes.ValuesByReceiver is { } modesByReceiver)
        {
            AssertReceiverTargets(capabilities, modesByReceiver.Keys);
            foreach (IReadOnlySet<RadioMode> modes in modesByReceiver.Values)
                AssertSubset(modes, capabilities.Modes.Values);
        }

        foreach ((RadioMode mode, PassbandConstraint constraint) in capabilities.Passband.ByMode)
        {
            Assert.Contains(mode, capabilities.Modes.Values);
            Assert.True(constraint.MinimumHz <= constraint.MaximumHz);
            Assert.True(constraint.StepHz > 0);
            if (constraint.DiscreteValuesHz is { } values)
                Assert.All(values, value => Assert.InRange(value, constraint.MinimumHz, constraint.MaximumHz));
        }

        AssertControls(capabilities, driver);
        AssertSwitches(capabilities, driver);
        AssertChoices(capabilities, driver);
        AssertMeters(capabilities, driver);
    }

    public static void AssertState(RadioCapabilities capabilities, RadioState state)
    {
        Assert.Contains(state.ActiveVfo, capabilities.Vfos.Available);
        Assert.Contains(state.TransmitVfo, capabilities.Vfos.Available);
        Assert.Contains(state.Mode, capabilities.Modes.Values);
        Assert.Contains(state.SelectedReceiver, capabilities.Receivers.Available.Keys);
        if (state.TransmitReceiver is ReceiverId transmitReceiver)
            Assert.Contains(transmitReceiver, capabilities.Receivers.Available.Keys);

        Assert.All(state.FrequenciesHz.Keys, vfo => Assert.Contains(vfo, capabilities.Vfos.Available));
        Assert.All(state.Vfos, pair =>
        {
            Assert.Contains(pair.Key, capabilities.Vfos.Available);
            Assert.Equal(pair.Key, pair.Value.Vfo);
            if (pair.Value.Mode is RadioMode mode)
                Assert.Contains(mode, capabilities.Modes.Values);
        });
        Assert.All(state.Receivers, pair =>
        {
            Assert.Contains(pair.Key, capabilities.Receivers.Available.Keys);
            Assert.Equal(pair.Key, pair.Value.Receiver);
            if (pair.Value.SelectedVfo is VfoId vfo)
                Assert.Contains(vfo, capabilities.Receivers.Available[pair.Key].AvailableVfos);
        });
        Assert.All(state.ReceivePaths, path => AssertSignalPath(capabilities, path));
        if (state.TransmitPath is RadioSignalPath transmitPath)
            AssertSignalPath(capabilities, transmitPath);
    }

    public static async Task AssertCoreMutationsAsync(
        IRadioDriver driver,
        RadioState initialState,
        CoreMutationScenario scenario,
        bool verifyRoundTrip = true)
    {
        RadioCapabilities capabilities = driver.Capabilities;
        AssertWritable(capabilities.Frequency.Feature);
        Assert.Contains(scenario.FrequencyTarget, capabilities.Frequency.Targets);
        Assert.True(capabilities.Frequency.CanReceive(scenario.FrequencyHz));
        AssertWritable(capabilities.Modes.Feature);
        Assert.Contains(scenario.Mode, capabilities.Modes.Values);
        AssertWritable(capabilities.Vfos.Split);
        Assert.NotEqual(initialState.IsSplit, scenario.SplitEnabled);

        await driver.SetFrequencyAsync(scenario.FrequencyTarget, scenario.FrequencyHz);
        await driver.SetModeAsync(scenario.Mode);
        await driver.SetSplitAsync(scenario.SplitEnabled);

        if (!verifyRoundTrip)
            return;

        RadioState changed = await driver.ReadStateAsync();
        AssertState(capabilities, changed);
        Assert.Equal(scenario.FrequencyHz, changed.FrequenciesHz[scenario.FrequencyTarget]);
        Assert.Equal(scenario.Mode, changed.Mode);
        Assert.Equal(scenario.SplitEnabled, changed.IsSplit);
    }

    public static async Task AssertFeatureMutationsAsync(
        IRadioDriver driver,
        FeatureMutationScenario scenario,
        RadioMode activeMode,
        bool verifyReadback = true)
    {
        RadioCapabilities capabilities = driver.Capabilities;
        NumericControlDescriptor numeric = capabilities.Controls[scenario.Numeric.Id];
        AssertWritable(numeric.Feature);
        Assert.InRange(scenario.Numeric.Value, numeric.Minimum, numeric.Maximum);
        Assert.Equal(0, (scenario.Numeric.Value - numeric.Minimum) % numeric.Step);

        SwitchControlDescriptor @switch = capabilities.Switches[scenario.Switch.Id];
        AssertWritable(@switch.Feature);

        ChoiceControlDescriptor choice = capabilities.Choices[scenario.Choice.Id];
        AssertWritable(choice.Feature);
        RadioChoiceOption option = choice.Options[scenario.Choice.Value];
        Assert.True(option.Writable);
        if (option.ApplicableModes is { } applicableModes)
            Assert.Contains(activeMode, applicableModes);

        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);
        var switches = Assert.IsAssignableFrom<IRadioSwitchDriver>(driver);
        var choices = Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);
        await controls.WriteControlAsync(scenario.Numeric.Id, scenario.Numeric.Value);
        await switches.WriteSwitchAsync(scenario.Switch.Id, scenario.Switch.Enabled);
        await choices.WriteChoiceAsync(scenario.Choice.Id, scenario.Choice.Value);

        IRadioPassbandDriver? passband = null;
        if (scenario.PassbandHz is int passbandHz)
        {
            AssertWritable(capabilities.Passband.Feature);
            PassbandConstraint constraint = capabilities.Passband.ByMode[activeMode];
            Assert.InRange(passbandHz, constraint.MinimumHz, constraint.MaximumHz);
            if (constraint.DiscreteValuesHz is { } values)
                Assert.Contains(passbandHz, values);
            else
                Assert.Equal(0, (passbandHz - constraint.MinimumHz) % constraint.StepHz);
            passband = Assert.IsAssignableFrom<IRadioPassbandDriver>(driver);
            await passband.SetPassbandAsync(passbandHz);
        }

        if (!verifyReadback)
            return;

        Assert.Equal(scenario.Numeric.Value,
            (await controls.ReadControlAsync(scenario.Numeric.Id)).Value);
        Assert.Equal(scenario.Switch.Enabled,
            (await switches.ReadSwitchAsync(scenario.Switch.Id)).Enabled);
        Assert.Equal(scenario.Choice.Value,
            (await choices.ReadChoiceAsync(scenario.Choice.Id)).Value);
        if (scenario.PassbandHz is int expectedPassbandHz)
            Assert.Equal(expectedPassbandHz, (await passband!.ReadPassbandAsync()).WidthHz);
    }

    public static async Task AssertFeatureReadsAsync(IRadioDriver driver, FeatureReadScenario scenario)
    {
        RadioCapabilities capabilities = driver.Capabilities;
        NumericControlDescriptor numericDescriptor = capabilities.Controls[scenario.Numeric];
        SwitchControlDescriptor switchDescriptor = capabilities.Switches[scenario.Switch];
        ChoiceControlDescriptor choiceDescriptor = capabilities.Choices[scenario.Choice];
        RadioMeterDescriptor meterDescriptor = capabilities.Meters[scenario.Meter];
        AssertReadable(numericDescriptor.Feature);
        AssertReadable(switchDescriptor.Feature);
        AssertReadable(choiceDescriptor.Feature);

        var controls = Assert.IsAssignableFrom<IRadioControlDriver>(driver);
        var switches = Assert.IsAssignableFrom<IRadioSwitchDriver>(driver);
        var choices = Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);
        var meters = Assert.IsAssignableFrom<IRadioMeterDriver>(driver);
        RadioControlValue numeric = await controls.ReadControlAsync(scenario.Numeric);
        RadioSwitchValue @switch = await switches.ReadSwitchAsync(scenario.Switch);
        RadioChoiceValue choice = await choices.ReadChoiceAsync(scenario.Choice);
        RadioMeterReading meter = await meters.ReadMeterAsync(scenario.Meter);

        Assert.Equal(scenario.Numeric, numeric.Id);
        Assert.InRange(numeric.Value, numericDescriptor.Minimum, numericDescriptor.Maximum);
        Assert.Equal(scenario.Switch, @switch.Id);
        Assert.Equal(scenario.Choice, choice.Id);
        Assert.Contains(choice.Value, choiceDescriptor.Options.Keys);
        Assert.Equal(scenario.Meter, meter.Id);
        Assert.InRange(meter.RawValue, meterDescriptor.RawMinimum, meterDescriptor.RawMaximum);
        Assert.InRange(meter.NormalizedValue, 0d, 1d);

        if (!scenario.IncludePassband)
            return;

        AssertReadable(capabilities.Passband.Feature);
        PassbandConstraint constraint = capabilities.Passband.ByMode[scenario.ActiveMode];
        RadioPassbandValue passband = await Assert.IsAssignableFrom<IRadioPassbandDriver>(driver)
            .ReadPassbandAsync();
        Assert.InRange(passband.WidthHz, constraint.MinimumHz, constraint.MaximumHz);
        if (constraint.DiscreteValuesHz is { } values)
            Assert.Contains(passband.WidthHz, values);
    }

    public static async Task AssertRejectsInvalidCoreMutationsAsync(IRadioDriver driver)
    {
        RadioCapabilities capabilities = driver.Capabilities;
        VfoId unsupportedVfo = Enum.GetValues<VfoId>()
            .First(vfo => vfo != VfoId.Current && !capabilities.Frequency.Targets.Contains(vfo));
        RadioMode unsupportedMode = Enum.GetValues<RadioMode>()
            .First(mode => !capabilities.Modes.Values.Contains(mode));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.SetFrequencyAsync(unsupportedVfo, capabilities.Frequency.Ranges[0].MinimumHz).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => driver.SetFrequencyAsync(capabilities.Frequency.Targets.First(), long.MaxValue).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.SetModeAsync(unsupportedMode).AsTask());
    }

    public static async Task AssertDriverOwnsTransportAsync(IRadioDriver driver, IRadioTransport transport)
    {
        Assert.True(transport.IsConnected);
        await driver.DisposeAsync();
        Assert.False(transport.IsConnected);
        await driver.DisposeAsync();
        Assert.False(transport.IsConnected);
    }

    public static async Task AssertUnknownModelDisposesTransportAsync(
        IRadioDriverFactory factory, IRadioTransport transport)
    {
        Assert.True(transport.IsConnected);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => factory.OpenAsync(OptionsForUnknownModel(), transport).AsTask());
        Assert.False(transport.IsConnected);
    }

    private static RadioConnectionOptions OptionsForUnknownModel() =>
        new("conformance", "unsupported.model",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static void AssertControls(RadioCapabilities capabilities, IRadioDriver driver)
    {
        if (capabilities.Controls.Count > 0)
            Assert.IsAssignableFrom<IRadioControlDriver>(driver);
        foreach (NumericControlDescriptor descriptor in capabilities.Controls.Values)
        {
            AssertFeature(descriptor.Feature);
            Assert.True(descriptor.Minimum <= descriptor.Maximum);
            Assert.True(descriptor.Step > 0);
            AssertSubset(descriptor.Targets, capabilities.Vfos.Available);
            AssertReceiverTargets(capabilities, descriptor.ReceiverTargets);
            AssertModes(capabilities, descriptor.ModeApplicability);
            if (descriptor.Targets.Count > 0)
                Assert.IsAssignableFrom<IRadioTargetedControlDriver>(driver);
            if (descriptor.ReceiverTargets.Count > 0)
                Assert.IsAssignableFrom<IRadioReceiverControlDriver>(driver);
        }
    }

    private static void AssertSwitches(RadioCapabilities capabilities, IRadioDriver driver)
    {
        if (capabilities.Switches.Count > 0)
            Assert.IsAssignableFrom<IRadioSwitchDriver>(driver);
        foreach (SwitchControlDescriptor descriptor in capabilities.Switches.Values)
        {
            AssertFeature(descriptor.Feature);
            AssertReceiverTargets(capabilities, descriptor.ReceiverTargets);
            AssertModes(capabilities, descriptor.ModeApplicability);
            if (descriptor.ReceiverTargets.Count > 0)
                Assert.IsAssignableFrom<IRadioReceiverSwitchDriver>(driver);
        }
    }

    private static void AssertChoices(RadioCapabilities capabilities, IRadioDriver driver)
    {
        if (capabilities.Choices.Count > 0)
            Assert.IsAssignableFrom<IRadioChoiceDriver>(driver);
        foreach (ChoiceControlDescriptor descriptor in capabilities.Choices.Values)
        {
            AssertFeature(descriptor.Feature);
            Assert.NotEmpty(descriptor.Options);
            AssertSubset(descriptor.Targets, capabilities.Vfos.Available);
            AssertReceiverTargets(capabilities, descriptor.ReceiverTargets);
            AssertModes(capabilities, descriptor.ModeApplicability);
            Assert.All(descriptor.Options.Values, option =>
            {
                Assert.False(string.IsNullOrWhiteSpace(option.Value));
                if (option.ApplicableModes is { } modes)
                    AssertSubset(modes, capabilities.Modes.Values);
            });
            if (descriptor.Targets.Count > 0)
                Assert.IsAssignableFrom<IRadioTargetedChoiceDriver>(driver);
            if (descriptor.ReceiverTargets.Count > 0)
                Assert.IsAssignableFrom<IRadioReceiverChoiceDriver>(driver);
            if (descriptor.OptionsByTarget is { } byTarget)
                AssertSubset(byTarget.Keys, descriptor.Targets);
            if (descriptor.OptionsByReceiver is { } byReceiver)
                AssertSubset(byReceiver.Keys, descriptor.ReceiverTargets);
        }
    }

    private static void AssertMeters(RadioCapabilities capabilities, IRadioDriver driver)
    {
        if (capabilities.Meters.Count > 0)
            Assert.IsAssignableFrom<IRadioMeterDriver>(driver);
        foreach (RadioMeterDescriptor descriptor in capabilities.Meters.Values)
        {
            Assert.True(descriptor.RawMinimum <= descriptor.RawMaximum);
            AssertModes(capabilities, descriptor.ModeApplicability);
            if (descriptor.RangesByTarget is { } byTarget)
            {
                AssertSubset(byTarget.Keys, capabilities.Vfos.Available);
                Assert.IsAssignableFrom<IRadioTargetedMeterDriver>(driver);
            }
            if (descriptor.RangesByReceiver is { } byReceiver)
            {
                AssertReceiverTargets(capabilities, byReceiver.Keys);
                Assert.IsAssignableFrom<IRadioReceiverMeterDriver>(driver);
            }
        }
    }

    private static void AssertFeature(FeatureDescriptor feature)
    {
        if (feature.Support is CapabilitySupport.Unsupported or CapabilitySupport.DriverNotImplemented)
            Assert.Equal(FeatureAccess.None, feature.Access);
        if (feature.Access != FeatureAccess.None)
            Assert.NotEqual(CapabilitySupport.Unsupported, feature.Support);
    }

    private static void AssertWritable(FeatureDescriptor feature) =>
        Assert.True(feature.Access.HasFlag(FeatureAccess.Write));

    private static void AssertReadable(FeatureDescriptor feature) =>
        Assert.True(feature.Access.HasFlag(FeatureAccess.Read));

    private static bool IsAvailable(FeatureDescriptor feature) =>
        (feature.Support is CapabilitySupport.Supported or CapabilitySupport.Experimental) &&
        feature.Access != FeatureAccess.None;

    private static void AssertSubset<T>(IEnumerable<T> subset, IEnumerable<T> set)
    {
        HashSet<T> available = set.ToHashSet();
        Assert.All(subset, value => Assert.Contains(value, available));
    }

    private static void AssertReceiverTargets(
        RadioCapabilities capabilities, IEnumerable<ReceiverId> receivers) =>
        Assert.All(receivers, receiver => Assert.Contains(receiver, capabilities.Receivers.Available.Keys));

    private static void AssertModes(RadioCapabilities capabilities, ModeApplicabilityDescriptor applicability)
    {
        if (applicability.ReadModes is { } readModes)
            AssertSubset(readModes, capabilities.Modes.Values);
        if (applicability.WriteModes is { } writeModes)
            AssertSubset(writeModes, capabilities.Modes.Values);
        if (applicability.OperationalModes is { } operationalModes)
            AssertSubset(operationalModes, capabilities.Modes.Values);
    }

    private static void AssertSignalPath(RadioCapabilities capabilities, RadioSignalPath path)
    {
        Assert.Contains(path.Receiver, capabilities.Receivers.Available.Keys);
        if (path.Vfo is VfoId vfo)
            Assert.Contains(vfo, capabilities.Receivers.Available[path.Receiver].AvailableVfos);
    }
}
