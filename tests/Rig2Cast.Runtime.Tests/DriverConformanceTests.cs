using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
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
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        DriverConformance.AssertCapabilities(driver);
        DriverConformance.AssertState(driver.Capabilities, await driver.ReadStateAsync());
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
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        DriverConformance.AssertCapabilities(driver);
        DriverConformance.AssertState(driver.Capabilities, await driver.ReadStateAsync());
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
        DriverConformance.AssertState(driver.Capabilities, await driver.ReadStateAsync());
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
        DriverConformance.AssertState(driver.Capabilities, await driver.ReadStateAsync());
    }

    private static async Task<InMemoryRadioTransport> ConnectedTransportAsync(string name)
    {
        var transport = new InMemoryRadioTransport(name);
        await transport.ConnectAsync();
        return transport;
    }
}

internal static class DriverConformance
{
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
