using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
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
        Assert.Equal(VfoId.A, state.Receivers[ReceiverId.Main].SelectedVfo);
        Assert.Equal(VfoId.B, state.Receivers[ReceiverId.Sub].SelectedVfo);
        Assert.Equal(14_275_000, state.Receivers[ReceiverId.Sub].FrequencyHz);
        Assert.Null(state.Receivers[ReceiverId.Sub].IsEnabled);
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
    public async Task ReceiverTargetedFrequencyAndModeMapToElecraftSignalPaths()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("IF;", "IF00014060000     +000000 0001000001 ;");
        transport.Add("FA00014101000;");
        transport.Add("FB00007075000;");
        transport.Add("IF;", "IF00014101000     +000000 0001000001 ;");
        transport.Add("MD3;");
        transport.Add("MD$2;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        await driver.SetFrequencyAsync(ReceiverId.Main, 14_101_000);
        await driver.SetFrequencyAsync(ReceiverId.Sub, 7_075_000);
        await driver.SetModeAsync(ReceiverId.Main, RadioMode.Cw);
        await driver.SetModeAsync(ReceiverId.Sub, RadioMode.Usb);

        Assert.Contains(ReceiverId.Main, driver.Capabilities.Frequency.ReceiverTargets);
        Assert.Contains(ReceiverId.Sub, driver.Capabilities.Frequency.ReceiverTargets);
        Assert.Contains(ReceiverId.Sub, driver.Capabilities.Modes.ReceiverTargets);
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
        StateInformationObservation observation = Assert.IsType<StateInformationObservation>(observations.Current);
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
        Assert.Single(driver.Capabilities.Receivers.Available);
        Assert.Contains(ReceiverId.Main, driver.Capabilities.Receivers.Available.Keys);
        Assert.DoesNotContain(ReceiverId.Sub, driver.Capabilities.Receivers.Available.Keys);
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
    public async Task ElecraftQueryValidatorRejectsSamePrefixFrameWithWrongShape()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("FA;", "FA1;FA00014250000;");
        await transport.ConnectAsync();
        await using var protocol = new ElecraftAsciiProtocol(transport);

        string response = await protocol.QueryAsync("FA", "FA", frame => frame.Length == 14);

        Assert.Equal("FA00014250000;", response);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<string> frames = protocol
            .WatchUnsolicitedFramesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("FA1;", frames.Current);
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
    public async Task CallerCancellationDoesNotInterruptStartedElecraftFrameWrite()
    {
        var transport = new BlockingWriteRadioTransport();
        await transport.ConnectAsync();
        await using var protocol = new ElecraftAsciiProtocol(transport);
        using var cancellation = new CancellationTokenSource();

        Task send = protocol.SendAsync("FA00014250000", cancellation.Token).AsTask();
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        Assert.False(send.IsCompleted);
        transport.CompleteWrite();
        await send;
        Assert.Equal("FA00014250000;", transport.WrittenFrame);
    }

    [Fact]
    public async Task SamePrefixElecraftFrameDuringWriteCannotCompleteQuery()
    {
        var transport = new BlockingWriteRadioTransport();
        await transport.ConnectAsync();
        await using var protocol = new ElecraftAsciiProtocol(transport);

        Task<string> query = protocol.QueryAsync("FA", "FA").AsTask();
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));
        await transport.EmitAsync("FA00007100000;");
        using var watchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using IAsyncEnumerator<string> frames = protocol
            .WatchUnsolicitedFramesAsync(watchTimeout.Token).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("FA00007100000;", frames.Current);
        Assert.False(query.IsCompleted);

        transport.CompleteWrite();
        await Task.Delay(20);
        await transport.EmitAsync("FA00014250000;");
        Assert.Equal("FA00014250000;", await query.WaitAsync(TimeSpan.FromSeconds(1)));
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

    [Theory]
    [InlineData("GT002;", "fast")]
    [InlineData("GT004;", "slow")]
    public async Task BasicGtFixtureContainsOnlyDocumentedTimeConstants(string response, string expected)
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("GT;", response);
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.Equal(expected, (await driver.ReadChoiceAsync(RadioChoiceId.Agc)).Value);
    }

    [Fact]
    public async Task UndocumentedBasicGt000IsRejected()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("GT;", "GT000;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        await Assert.ThrowsAsync<ElecraftProtocolException>(
            () => driver.ReadChoiceAsync(RadioChoiceId.Agc).AsTask());
    }

    [Theory]
    [InlineData(ElecraftK3Profile.K3ModelId, "OM-P-S--------;", "RA$01;")]
    [InlineData(ElecraftK3Profile.K3SModelId, "OM-P-S---LVR--;", "RA$10;")]
    public async Task SubReceiverAttenuatorFixtureAcceptsModelSpecificTenDbFormat(
        string modelId, string optionResponse, string response)
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", optionResponse);
        transport.Add("RA$;", response);
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[modelId]);

        RadioChoiceValue attenuator = await driver.ReadChoiceAsync(RadioChoiceId.Attenuator, VfoId.B);
        Assert.Equal("10db", attenuator.Value);
        Assert.Equal(VfoId.B, attenuator.Target);
    }

    [Theory]
    [InlineData(ElecraftK3Profile.K3ModelId, "OM-P-S--------;", "RA$01;")]
    [InlineData(ElecraftK3Profile.K3SModelId, "OM-P-S---LVR--;", "RA$10;")]
    public async Task SubReceiverAttenuatorWritesModelSpecificTenDbFormat(
        string modelId, string optionResponse, string expectedCommand)
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", optionResponse);
        transport.Add(expectedCommand);
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[modelId]);

        await driver.WriteChoiceAsync(RadioChoiceId.Attenuator, VfoId.B, "10db");

        transport.AssertComplete();
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
        ReceiverCapability sub = driver.Capabilities.Receivers.Available[ReceiverId.Sub];
        Assert.True(sub.IsOptional);
        Assert.True(sub.SupportsSimultaneousReception);
        Assert.Equal(new HashSet<VfoId> { VfoId.B }, sub.AvailableVfos);
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
        Assert.True(driver.Capabilities.Meters[RadioMeterId.Swr].RequiresTransmit);
    }

    [Fact]
    public async Task FirmwareBefore566DoesNotAdvertiseOrQuerySwr()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("RVM;", "RVM05.62;");
        await using ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);

        Assert.False(driver.Capabilities.Meters.ContainsKey(RadioMeterId.Swr));
        Assert.Equal("5.62", driver.Capabilities.Extensions["elecraft.firmwareVersion"]);
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.ReadMeterAsync(RadioMeterId.Swr).AsTask());
        Assert.Contains("5.66", error.Message, StringComparison.Ordinal);
        transport.AssertComplete();
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
        Assert.Equal(123, Assert.IsType<NumericControlChangedObservation>(observations.Current).Control.Value);
        Assert.True(await observations.MoveNextAsync());
        Assert.True(Assert.IsType<SwitchControlChangedObservation>(observations.Current).Control.Enabled);
        Assert.True(await observations.MoveNextAsync());
        Assert.Equal("5db", Assert.IsType<ChoiceControlChangedObservation>(observations.Current).Control.Value);
        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(2_400, Assert.IsType<PassbandChangedObservation>(observations.Current).Passband.WidthHz);
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
        RadioControlValue control = Assert.IsType<NumericControlChangedObservation>(observations.Current).Control;
        Assert.Equal(RadioControlId.RfGain, control.Id);
        Assert.Equal(VfoId.B, control.Target);
        Assert.Equal(ReceiverId.Sub, control.Receiver);
        Assert.True(await observations.MoveNextAsync());
        control = Assert.IsType<NumericControlChangedObservation>(observations.Current).Control;
        Assert.Equal(RadioControlId.AfGain, control.Id);
        Assert.Equal(VfoId.B, control.Target);
        Assert.Equal(ReceiverId.Sub, control.Receiver);
        Assert.True(await observations.MoveNextAsync());
        control = Assert.IsType<NumericControlChangedObservation>(observations.Current).Control;
        Assert.Equal(RadioControlId.KeyerSpeedWpm, control.Id);
        Assert.Null(control.Target);
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
        Assert.IsType<UnknownFrameObservation>(observations.Current);
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

        Assert.Equal(36, (await driver.ReadControlAsync(RadioControlId.AfGain, ReceiverId.Sub)).Value);
        await driver.WriteControlAsync(RadioControlId.RfGain, ReceiverId.Sub, 200);
        Assert.Equal("10db", (await driver.ReadChoiceAsync(RadioChoiceId.Attenuator, ReceiverId.Sub)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.Preamp, ReceiverId.Sub, "preamp1");
        Assert.Equal(2_400, (await driver.ReadPassbandAsync(ReceiverId.Sub)).WidthHz);
        await driver.SetPassbandAsync(ReceiverId.Sub, 2_700);
        RadioMeterReading meter = await driver.ReadMeterAsync(RadioMeterId.SignalStrength, ReceiverId.Sub);

        Assert.Equal(9, meter.RawValue);
        Assert.Equal(VfoId.B, meter.Target);
        Assert.Equal(ReceiverId.Sub, meter.Receiver);
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
        Assert.Contains(ReceiverId.Sub, driver.Capabilities.Controls[RadioControlId.AfGain].ReceiverTargets);
        Assert.Contains(VfoId.B, driver.Capabilities.Passband.Targets);
        Assert.Contains(ReceiverId.Sub, driver.Capabilities.Passband.ReceiverTargets);
        ChoiceControlDescriptor attenuator = driver.Capabilities.Choices[RadioChoiceId.Attenuator];
        Assert.Equal(["off", "10db"], attenuator.OptionsByTarget![VfoId.B].Keys);
        Assert.Equal(["off", "10db"], attenuator.OptionsByReceiver![ReceiverId.Sub].Keys);
        RadioMeterRange subMeter = driver.Capabilities.Meters[RadioMeterId.SignalStrength].RangesByTarget![VfoId.B];
        Assert.Equal(15, subMeter.RawMaximum);
        Assert.Equal(15, driver.Capabilities.Meters[RadioMeterId.SignalStrength]
            .RangesByReceiver![ReceiverId.Sub].RawMaximum);
        Assert.DoesNotContain(VfoId.B, driver.Capabilities.Meters[RadioMeterId.Swr].RangesByTarget!.Keys);
        Assert.DoesNotContain(ReceiverId.Sub,
            driver.Capabilities.Meters[RadioMeterId.Swr].RangesByReceiver!.Keys);
    }

    [Fact]
    public async Task ManagedSessionRoutesReceiverTargetedReadThroughScheduler()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("OM;", "OM-P-S---LVR--;");
        transport.Add("FA;", "FA00014250000;");
        transport.Add("FB;", "FB00014275000;");
        transport.Add("IF;", "IF00014250000     +000000 0002001001 ;");
        transport.Add("FT;", "FT1;");
        transport.Add("TQ;", "TQ0;");
        transport.Add("AG$;", "AG$036;");
        ElecraftK3Driver driver = await ElecraftK3Driver.OpenAsync(
            transport, ElecraftK3Profile.Models[ElecraftK3Profile.K3SModelId]);
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("k3s", driver);
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("receiver-client"));

        RadioControlValue value = await session.ReadControlAsync(RadioControlId.AfGain, ReceiverId.Sub);

        Assert.Equal(36, value.Value);
        Assert.Equal(ReceiverId.Sub, value.Receiver);
        transport.AssertComplete();
    }
}
