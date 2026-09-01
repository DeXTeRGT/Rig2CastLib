using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Elecraft.Protocol;
using Rig2Cast.Runtime.Sessions;

namespace Rig2Cast.Runtime.Tests;

public sealed class ElecraftK3DriverTests
{
    [Fact]
    public void FactoryPublishesFourSelectableModels()
    {
        var factory = new ElecraftK3DriverFactory();

        Assert.Equal(4, factory.Descriptor.Models.Count);
        Assert.Contains(factory.Descriptor.Models, model => model.Id == ElecraftK3Profile.K3SModelId);
        Assert.Contains(factory.Descriptor.Models, model => model.Id == ElecraftK3Profile.K3ModelId);
        Assert.Contains(factory.Descriptor.Models, model => model.Id == ElecraftK3Profile.KX3ModelId);
        Assert.Contains(factory.Descriptor.Models, model => model.Id == ElecraftK3Profile.KX2ModelId);
        Assert.All(factory.Descriptor.Models, model => Assert.Equal(38_400, model.DefaultBaudRate));
    }

    [Theory]
    [InlineData(ElecraftK3Profile.K3SModelId, "OM-P-S----VR--;", true)]
    [InlineData(ElecraftK3Profile.K3ModelId, "OM-P-S--------;", true)]
    [InlineData(ElecraftK3Profile.KX2ModelId, "OMAPF---TBXI01;", true)]
    [InlineData(ElecraftK3Profile.KX3ModelId, "OMAPF---TBXI02;", true)]
    [InlineData(ElecraftK3Profile.K3SModelId, "OM-P-S--------;", false)]
    public void OptionResponseIdentifiesRequestedModel(string modelId, string response, bool expected)
    {
        Assert.Equal(expected, ElecraftK3Profile.Models[modelId].MatchesOptionResponse(response));
    }

    [Fact]
    public async Task ReadsCoreStateIncludingExplicitTransmitVfo()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S----VR--;" );
        transport.Add("FA;", "FA00014250000;");
        transport.Add("FB;", "FB00014275000;");
        transport.Add("IF;", "IF00014250000     +000000 0002001001 ;");
        transport.Add("FT;", "FT1;");
        transport.Add("TQ;", "TQ0;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        RadioState state = await driver.ReadStateAsync();

        Assert.Equal(14_250_000, state.FrequenciesHz[VfoId.A]);
        Assert.Equal(14_275_000, state.FrequenciesHz[VfoId.B]);
        Assert.Equal(VfoId.A, state.ActiveVfo);
        Assert.Equal(VfoId.B, state.TransmitVfo);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.True(state.IsSplit);
        Assert.False(state.IsTransmitting);
    }

    [Fact]
    public async Task WritesDocumentedCoreCommands()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S----VR--;" );
        transport.Add("FA00014060000;");
        transport.Add("FB00014100000;");
        transport.Add("IF;", "IF00014060000     +000000 0001000001 ;");
        transport.Add("MD3;");
        transport.Add("FT1;");
        transport.Add("FR0;");
        transport.Add("TX;");
        transport.Add("RX;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        await driver.SetFrequencyAsync(VfoId.A, 14_060_000);
        await driver.SetFrequencyAsync(VfoId.B, 14_100_000);
        await driver.SetModeAsync(RadioMode.Cw);
        await driver.SetSplitAsync(true, VfoId.B);
        await driver.SetSplitAsync(false, VfoId.A);
        await driver.SetPttAsync(true);
        await driver.SetPttAsync(false);

        transport.AssertComplete();
    }

    [Fact]
    public async Task Ai1InformationBecomesCompleteTypedObservation()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S----VR--;" );
        transport.Add("K31;");
        transport.Add("AI1;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport,
            ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId],
            enableAutomaticInformation: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("IF00014250000     +000000 1012001001 ;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        RadioDriverObservation observation = observations.Current;
        Assert.Equal(RadioDriverObservationKind.StateInformation, observation.Kind);
        Assert.Equal(14_250_000, observation.FrequencyHz);
        Assert.Equal(RadioMode.Usb, observation.Mode);
        Assert.Equal(VfoId.B, observation.TransmitVfo);
        Assert.True(observation.IsSplit);
        Assert.True(observation.IsTransmitting);
    }

    [Fact]
    public async Task Kx2CapabilitiesExcludeFm()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OMAPF---TBXI01;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.KX2ModelId]);

        Assert.DoesNotContain(RadioMode.Fm, driver.Capabilities.Modes.Values);
        Assert.Throws<NotSupportedException>(() =>
            ElecraftK3Profile.Models[ElecraftK3Profile.KX2ModelId].EncodeMode(RadioMode.Fm));
    }

    [Fact]
    public async Task ProtocolRoutesUnsolicitedFrameAroundPendingQuery()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "FA00014250000;", "OM-P-S----VR--;");
        await transport.ConnectAsync();
        await using var protocol = new ElecraftAsciiProtocol(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<string> unsolicited = protocol
            .WatchUnsolicitedFramesAsync(timeout.Token)
            .GetAsyncEnumerator();

        string response = await protocol.QueryAsync("OM", "OM", timeout.Token);

        Assert.Equal("OM-P-S----VR--;", response);
        Assert.True(await unsolicited.MoveNextAsync());
        Assert.Equal("FA00014250000;", unsolicited.Current);
    }

    [Fact]
    public async Task BusyResponseRejectsQueryWithoutFaultingProtocolSession()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "?;");
        transport.Add("FA;", "FA00014250000;");
        await transport.ConnectAsync();
        await using var protocol = new ElecraftAsciiProtocol(transport);

        ElecraftCommandRejectedException exception = await Assert.ThrowsAsync<ElecraftCommandRejectedException>(
            async () => await protocol.QueryAsync("OM", "OM"));

        Assert.Equal("OM;", exception.Command);
        Assert.Equal("FA00014250000;", await protocol.QueryAsync("FA", "FA"));
    }

    [Fact]
    public async Task ManagedRadioShutdownClosesTransportBeforeAwaitingNonCancelableRead()
    {
        var transport = new ScriptedRadioTransport(ignoreReadCancellation: true);
        transport.Add("OM;", "OM-P-S----VR--;");
        transport.Add("FA;", "FA00014250000;");
        transport.Add("FB;", "FB00014275000;");
        transport.Add("IF;", "IF00014250000     +000000 0002000001 ;");
        transport.Add("FT;", "FT0;");
        transport.Add("TQ;", "TQ0;");
        ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);
        ManagedRadio radio = await ManagedRadio.CreateAsync("elecraft-test", driver);

        await radio.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ReadsSecondMilestoneControlsSwitchesAndChoices()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("AG;", "AG123;");
        transport.Add("RG;", "RG200;");
        transport.Add("PC;", "PC001;");
        transport.Add("RO;", "RO-0123;");
        transport.Add("KS;", "KS027;");
        transport.Add("RT;", "RT1;");
        transport.Add("XT;", "XT0;");
        transport.Add("GT;", "GT002;");
        transport.Add("RA;", "RA10;");
        transport.Add("PA;", "PA1;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.Equal(123, (await driver.ReadControlAsync(RadioControlId.AfGain)).Value);
        Assert.Equal(200, (await driver.ReadControlAsync(RadioControlId.RfGain)).Value);
        Assert.Equal(1, (await driver.ReadControlAsync(RadioControlId.TransmitPower)).Value);
        Assert.Equal(-123, (await driver.ReadControlAsync(RadioControlId.ClarifierOffsetHz)).Value);
        Assert.Equal(27, (await driver.ReadControlAsync(RadioControlId.KeyerSpeedWpm)).Value);
        Assert.True((await driver.ReadSwitchAsync(RadioSwitchId.ReceiveClarifier)).Enabled);
        Assert.False((await driver.ReadSwitchAsync(RadioSwitchId.TransmitClarifier)).Enabled);
        Assert.Equal("fast", (await driver.ReadChoiceAsync(RadioChoiceId.Agc)).Value);
        Assert.Equal("10db", (await driver.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
        Assert.Equal("preamp1", (await driver.ReadChoiceAsync(RadioChoiceId.Preamp)).Value);
    }

    [Fact]
    public async Task WritesSecondMilestoneControlsSwitchesAndChoices()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("AG128;");
        transport.Add("RG210;");
        transport.Add("PC001;");
        transport.Add("RO+0250;");
        transport.Add("KS030;");
        transport.Add("RT1;");
        transport.Add("XT0;");
        transport.Add("GT004;");
        transport.Add("RA05;");
        transport.Add("PA2;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        await driver.WriteControlAsync(RadioControlId.AfGain, 128);
        await driver.WriteControlAsync(RadioControlId.RfGain, 210);
        await driver.WriteControlAsync(RadioControlId.TransmitPower, 1);
        await driver.WriteControlAsync(RadioControlId.ClarifierOffsetHz, 250);
        await driver.WriteControlAsync(RadioControlId.KeyerSpeedWpm, 30);
        await driver.WriteSwitchAsync(RadioSwitchId.ReceiveClarifier, true);
        await driver.WriteSwitchAsync(RadioSwitchId.TransmitClarifier, false);
        await driver.WriteChoiceAsync(RadioChoiceId.Agc, "slow");
        await driver.WriteChoiceAsync(RadioChoiceId.Attenuator, "5db");
        await driver.WriteChoiceAsync(RadioChoiceId.Preamp, "preamp2");

        transport.AssertComplete();
    }

    [Fact]
    public async Task ConnectedOptionsShapeK3sCapabilities()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.Equal(110, driver.Capabilities.Controls[RadioControlId.TransmitPower].Maximum);
        Assert.Contains("5db", driver.Capabilities.Choices[RadioChoiceId.Attenuator].Options.Keys);
        Assert.Contains("15db", driver.Capabilities.Choices[RadioChoiceId.Attenuator].Options.Keys);
        Assert.Contains("preamp2", driver.Capabilities.Choices[RadioChoiceId.Preamp].Options.Keys);
        Assert.Contains(RadioSwitchId.ReceiveClarifier, driver.Capabilities.Switches.Keys);
    }

    [Fact]
    public async Task ReadsAndWritesNumericQuantizedPassband()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("BW;", "BW0240;");
        transport.Add("BW0270;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.Equal(2_400, (await driver.ReadPassbandAsync()).WidthHz);
        await driver.SetPassbandAsync(2_700);

        PassbandConstraint constraint = driver.Capabilities.Passband.ByMode[RadioMode.Usb];
        Assert.Equal(10, constraint.StepHz);
        Assert.True(constraint.RadioMayQuantize);
        Assert.Null(constraint.DiscreteValuesHz);
        transport.AssertComplete();
    }

    [Fact]
    public async Task K3sUsesHighResolutionSignalMeterAndReadsProtocolDefinedSwr()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("SMH;", "SMH040;");
        transport.Add("SW;", "SW015;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        RadioMeterReading signal = await driver.ReadMeterAsync(RadioMeterId.SignalStrength);
        RadioMeterReading swr = await driver.ReadMeterAsync(RadioMeterId.Swr);

        Assert.Equal(40, signal.RawValue);
        Assert.Equal(15, swr.RawValue);
        Assert.Equal("raw SMH", driver.Capabilities.Meters[RadioMeterId.SignalStrength].RawUnit);
        Assert.Equal("0.1 SWR", driver.Capabilities.Meters[RadioMeterId.Swr].RawUnit);
    }

    [Fact]
    public async Task PortableProfileUsesBasicSignalMeter()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OMAPF---TBXI02;");
        transport.Add("SM;", "SM0009;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.KX3ModelId]);

        Assert.Equal(9, (await driver.ReadMeterAsync(RadioMeterId.SignalStrength)).RawValue);
        Assert.Equal(15, driver.Capabilities.Meters[RadioMeterId.SignalStrength].RawMaximum);
    }

    [Fact]
    public async Task ControlAnnouncementsBecomeTypedObservations()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("K31;");
        transport.Add("AI2;");
        transport.Add("AI0;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport,
            ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId],
            enableAutomaticInformation: true,
            automaticInformationMode: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("AG123;RT1;RA05;FW0240;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(123, observations.Current.NumericControl?.Value);
        Assert.True(await observations.MoveNextAsync());
        Assert.True(observations.Current.SwitchControl?.Enabled);
        Assert.True(await observations.MoveNextAsync());
        Assert.Equal("5db", observations.Current.ChoiceControl?.Value);
        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(2_400, observations.Current.Passband?.WidthHz);
    }

    [Fact]
    public async Task DollarSuffixedGainAnnouncementsRetainSubReceiverTarget()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("K31;");
        transport.Add("AI2;");
        transport.Add("AI0;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport,
            ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId],
            enableAutomaticInformation: true,
            automaticInformationMode: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("RG$162;AG$036;KS027;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(RadioControlId.RfGain, observations.Current.NumericControl?.Id);
        Assert.Equal(VfoId.B, observations.Current.NumericControl?.Target);
        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(RadioControlId.AfGain, observations.Current.NumericControl?.Id);
        Assert.Equal(VfoId.B, observations.Current.NumericControl?.Target);
        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(RadioControlId.KeyerSpeedWpm, observations.Current.NumericControl?.Id);
        Assert.Null(observations.Current.NumericControl?.Target);
    }

    [Fact]
    public async Task LegacyLengthFwAnnouncementDoesNotPublishFalseZeroPassband()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("K31;");
        transport.Add("AI2;");
        transport.Add("AI0;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport,
            ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId],
            enableAutomaticInformation: true,
            automaticInformationMode: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("FW00000;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(RadioDriverObservationKind.Unknown, observations.Current.Kind);
        Assert.Null(observations.Current.Passband);
    }

    [Fact]
    public async Task SubReceiverOperationsUseDollarSuffixedCommands()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("AG$;", "AG$036;");
        transport.Add("RG$200;");
        transport.Add("RA$;", "RA$10;");
        transport.Add("PA$1;");
        transport.Add("BW$;", "BW$0240;");
        transport.Add("BW$0270;");
        transport.Add("SM$;", "SM$0009;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.Equal(36, (await driver.ReadControlAsync(RadioControlId.AfGain, VfoId.B)).Value);
        await driver.WriteControlAsync(RadioControlId.RfGain, VfoId.B, 200);
        Assert.Equal("10db", (await driver.ReadChoiceAsync(RadioChoiceId.Attenuator, VfoId.B)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.Preamp, VfoId.B, "preamp1");
        Assert.Equal(2_400, (await driver.ReadPassbandAsync(VfoId.B)).WidthHz);
        await driver.SetPassbandAsync(VfoId.B, 2_700);
        RadioMeterReading meter = await driver.ReadMeterAsync(RadioMeterId.SignalStrength, VfoId.B);

        Assert.Equal(9, meter.RawValue);
        Assert.Equal(VfoId.B, meter.Target);
        transport.AssertComplete();
    }

    [Fact]
    public async Task CapabilitiesDescribeTargetSpecificSubReceiverLimits()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.Contains(VfoId.B, driver.Capabilities.Controls[RadioControlId.AfGain].Targets);
        Assert.Contains(VfoId.B, driver.Capabilities.Passband.Targets);
        ChoiceControlDescriptor attenuator = driver.Capabilities.Choices[RadioChoiceId.Attenuator];
        Assert.Equal(["off", "10db"], attenuator.OptionsByTarget![VfoId.B].Keys);
        RadioMeterRange subMeter = driver.Capabilities.Meters[RadioMeterId.SignalStrength].RangesByTarget![VfoId.B];
        Assert.Equal(15, subMeter.RawMaximum);
        Assert.DoesNotContain(VfoId.B, driver.Capabilities.Meters[RadioMeterId.Swr].RangesByTarget!.Keys);
    }
}
