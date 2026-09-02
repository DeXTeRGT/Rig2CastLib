using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Drivers.Yaesu.Protocol;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;
using Capabilities = Rig2Cast.Abstractions.Capabilities;

namespace Rig2Cast.Runtime.Tests;

public sealed class Ftdx10DriverTests
{
    [Fact]
    public async Task DisposeClosesTransportBeforeWaitingForCancellationInsensitiveReader()
    {
        var transport = new ScriptedRadioTransport(ignoreReadCancellation: true);
        transport.Add("ID;", "ID0761;");
        Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        await driver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ConcurrentDisposeClaimsDriverCleanupExactlyOnce()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => driver.DisposeAsync().AsTask()));

        Assert.Equal(1, transport.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => driver.ReadStateAsync().AsTask());
    }

    [Fact]
    public async Task UnsolicitedFrequencyFrameBecomesTypedObservation()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        IRadioObservationSource observationSource = Assert.IsAssignableFrom<IRadioObservationSource>(driver);
        await using IAsyncEnumerator<RadioDriverObservation> observations = observationSource
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("FA014300000;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        FrequencyChangedObservation observation = Assert.IsType<FrequencyChangedObservation>(observations.Current);
        Assert.Equal(VfoId.A, observation.Vfo);
        Assert.Equal(14_300_000, observation.FrequencyHz);
    }

    [Fact]
    public async Task FactoryTimeProviderControlsObservationTimestamp()
    {
        var expected = new DateTimeOffset(2042, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var clock = new FixedTimeProvider(expected);
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        var factory = new Ftdx10DriverFactory(clock);
        await using IRadioDriver driver = await factory.OpenAsync(
            new RadioConnectionOptions("radio-1", Ftdx10CatProfile.ModelId,
                new Dictionary<string, string>()),
            transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        IRadioObservationSource observationSource = Assert.IsAssignableFrom<IRadioObservationSource>(driver);
        await using IAsyncEnumerator<RadioDriverObservation> observations = observationSource
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("FA014300000;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(expected, observations.Current.ObservedAt);
    }

    [Fact]
    public async Task AutomaticInformationIsExplicitlyEnabledConfirmedAndDisabled()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("AI1;");
        transport.Add("AI;", "FA014300000;AI1;");
        transport.Add("AI0;");
        Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(
            transport,
            enableAutomaticInformation: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(RadioDriverObservationKind.FrequencyChanged, observations.Current.Kind);

        await driver.DisposeAsync();
        transport.AssertComplete();
    }

    [Theory]
    [InlineData("IF001014249788+000000100000;", RadioDriverObservationKind.StateInformation, 14249788, RadioMode.Lsb)]
    [InlineData("IF001014249788+000000200000;", RadioDriverObservationKind.StateInformation, 14249788, RadioMode.Usb)]
    public async Task InformationAnnouncementDecodesFrequencyAndMode(
        string frame,
        RadioDriverObservationKind expectedKind,
        long expectedFrequency,
        RadioMode expectedMode)
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync(frame, timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        StateInformationObservation observation = Assert.IsType<StateInformationObservation>(observations.Current);
        Assert.Equal(expectedKind, observation.Kind);
        Assert.Equal(expectedFrequency, observation.FrequencyHz);
        Assert.Equal(expectedMode, observation.Mode);
        // Yaesu CAT 2308-F: IF reports the VFO-A frequency and operating mode,
        // but does not report selected A/B VFO or split state.
        Assert.Equal(VfoId.A, observation.Vfo);
        Assert.Null(observation.ActiveVfo);
        Assert.Null(observation.IsSplit);
        Assert.Null(observation.TransmitVfo);
    }

    [Fact]
    public async Task SpectrumDisplayFrequencyAnnouncementIsRecognizedButIgnored()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("FD001014217209;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        IgnoredFrameObservation observation = Assert.IsType<IgnoredFrameObservation>(observations.Current);
        Assert.Equal("FD001014217209;", observation.RawFrame);
    }

    [Fact]
    public async Task AutomaticMeterSelectionAnnouncementIsRecognizedAndIgnored()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioDriverObservation> observations = driver
            .WatchObservationsAsync(timeout.Token)
            .GetAsyncEnumerator();

        await transport.EmitAsync("RM0006000;", timeout.Token);

        Assert.True(await observations.MoveNextAsync());
        Assert.Equal(RadioDriverObservationKind.Ignored, observations.Current.Kind);
    }

    [Fact]
    public async Task UnsolicitedFrequencyUpdatesManagedStateAndClients()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("FA;", "FA014250000;");
        transport.Add("FB;", "FB007100000;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("ST;", "ST0;");
        transport.Add("TX;", "TX0;");
        Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", driver);
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("gui"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<RadioEvent> events = session
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator();
        Task<bool> nextEvent = events.MoveNextAsync().AsTask();

        await transport.EmitAsync("FA014300000;", timeout.Token);

        Assert.True(await nextEvent);
        Assert.Equal(RadioEventKind.StateChanged, events.Current.Kind);
        RadioState state = (await session.GetSnapshotAsync(timeout.Token)).State;
        Assert.Equal(14_300_000, state.FrequenciesHz[VfoId.A]);
        Assert.Equal(14_300_000, state.Vfos[VfoId.A].FrequencyHz);
        Assert.Equal(14_300_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.True(state.Revision > 1);
    }

    [Fact]
    public async Task OpenVerifiesIdentificationAndReadsState()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("FA;", "FA014250000;");
        transport.Add("FB;", "FB007100000;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("ST;", "ST1;");
        transport.Add("TX;", "TX0;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        RadioState state = await driver.ReadStateAsync();

        Assert.Equal(14_250_000, state.FrequenciesHz[VfoId.A]);
        Assert.Equal(7_100_000, state.FrequenciesHz[VfoId.B]);
        Assert.Equal(VfoId.A, state.ActiveVfo);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.True(state.IsSplit);
        Assert.Equal(VfoId.B, state.TransmitVfo);
        Assert.False(state.IsTransmitting);
        Assert.Equal(ReceiverId.Main, state.SelectedReceiver);
        Assert.Equal(VfoId.A, state.Receivers[ReceiverId.Main].SelectedVfo);
        Assert.Equal(14_250_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(7_100_000, state.Vfos[VfoId.B].FrequencyHz);
        Assert.Equal(
            [new RadioSignalPath(ReceiverId.Main, VfoId.A)],
            state.ReceivePaths);
        Assert.Equal(
            new RadioSignalPath(ReceiverId.Main, VfoId.B),
            state.TransmitPath);
        Assert.Single(driver.Capabilities.Receivers.Available);
        Assert.Contains(ReceiverId.Main, driver.Capabilities.Receivers.Available.Keys);
        transport.AssertComplete();
    }

    [Fact]
    public async Task NonSplitStateUsesTheReceivePathForTransmit()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("FA;", "FA014250000;");
        transport.Add("FB;", "FB007100000;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("ST;", "ST0;");
        transport.Add("TX;", "TX0;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        RadioState state = await driver.ReadStateAsync();

        Assert.False(state.IsSplit);
        Assert.Equal(VfoId.B, state.TransmitVfo);
        Assert.Equal(
            new RadioSignalPath(ReceiverId.Main, VfoId.A),
            state.TransmitPath);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ExplicitSplitAcceptsOnlyOppositeTransmitVfo()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("VS;", "VS0;");
        transport.Add("ST1;");
        transport.Add("VS;", "VS0;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        await driver.SetSplitAsync(true, VfoId.B);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => driver.SetSplitAsync(true, VfoId.A).AsTask());

        transport.AssertComplete();
    }

    [Fact]
    public async Task MutationsUseDocumentedYaesuCommands()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("FA014225000;");
        transport.Add("FB007125000;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD03;");
        transport.Add("ST1;");
        transport.Add("TX1;");
        transport.Add("TX0;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        await driver.SetFrequencyAsync(VfoId.A, 14_225_000);
        await driver.SetFrequencyAsync(VfoId.B, 7_125_000);
        await driver.SetModeAsync(RadioMode.Cw);
        await driver.SetSplitAsync(true);
        await driver.SetPttAsync(true);
        await driver.SetPttAsync(false);

        transport.AssertComplete();
    }

    [Fact]
    public async Task ActiveVfoSelectionUsesDocumentedCommand()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("VS1;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        await driver.SetActiveVfoAsync(VfoId.B);

        Assert.True(driver.Capabilities.Vfos.Selection.Access.HasFlag(Capabilities.FeatureAccess.Write));
        transport.AssertComplete();
    }

    [Fact]
    public async Task FrequencyCapabilitiesDistinguishReceiveCoverageFromTransmitCoverage()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.True(driver.Capabilities.Frequency.CanReceive(14_250_000));
        Assert.False(driver.Capabilities.Frequency.CanReceive(29_999));
        Assert.False(driver.Capabilities.Frequency.CanTransmit(14_250_000));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => driver.SetFrequencyAsync(VfoId.A, 29_999).AsTask());
        transport.AssertComplete();
    }

    [Fact]
    public async Task OpenRejectsDifferentRadioIdentification()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID9999;");

        await Assert.ThrowsAsync<YaesuProtocolException>(() => Ftdx10Driver.OpenAsync(transport).AsTask());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ReadsAndWritesTypedNumericControls()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("AG0;", "AG0128;");
        transport.Add("PC;", "PC075;");
        transport.Add("AG0200;");
        transport.Add("PC050;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(128, (await driver.ReadControlAsync(RadioControlId.AfGain)).Value);
        Assert.Equal(75, (await driver.ReadControlAsync(RadioControlId.TransmitPower)).Value);
        await driver.WriteControlAsync(RadioControlId.AfGain, 200);
        await driver.WriteControlAsync(RadioControlId.TransmitPower, 50);

        Assert.Equal(18, driver.Capabilities.Controls.Count);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAllDocumentedRawMeters()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("SM0;", "SM0123;");
        transport.Add("RM3;", "RM3064000;");
        transport.Add("RM4;", "RM4075000;");
        transport.Add("RM5;", "RM5086000;");
        transport.Add("RM6;", "RM6097000;");
        transport.Add("RM7;", "RM7108000;");
        transport.Add("RM8;", "RM8119000;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        int[] readings = [];
        var values = new List<int>();
        foreach (RadioMeterId meter in Enum.GetValues<RadioMeterId>())
        {
            values.Add((await driver.ReadMeterAsync(meter)).RawValue);
        }

        readings = values.ToArray();
        Assert.Equal([123, 64, 75, 86, 97, 108, 119], readings);
        // Yaesu CAT 2308-F defines RM as selector + three-digit P2 value +
        // fixed P3="000". The fixed suffix is not part of the meter value.
        Assert.Equal(64d / 255d,
            (await ReadSingleMeterAsync(RadioMeterId.Compression, "RM3064000;")).NormalizedValue,
            6);
        Assert.All(driver.Capabilities.Meters.Values, descriptor => Assert.False(descriptor.CalibrationAvailable));
        transport.AssertComplete();
    }

    private static async Task<RadioMeterReading> ReadSingleMeterAsync(RadioMeterId meter, string response)
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        string query = meter switch
        {
            RadioMeterId.Compression => "RM3;",
            _ => throw new ArgumentOutOfRangeException(nameof(meter))
        };
        transport.Add(query, response);
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);
        return await driver.ReadMeterAsync(meter);
    }

    [Fact]
    public async Task ReadsAndWritesTypedSwitches()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("NB0;", "NB01;");
        transport.Add("NR0;", "NR01;");
        transport.Add("ML0;", "ML0001;");
        transport.Add("PR0;", "PR01;");
        transport.Add("VX;", "VX1;");
        transport.Add("LK;", "LK0;");
        transport.Add("BI;", "BI0;");
        transport.Add("AC;", "AC001;");
        transport.Add("NA0;", "NA00;");
        transport.Add("BC0;", "BC01;");
        transport.Add("BP00;", "BP00001;");
        transport.Add("CO00;", "CO000001;");
        transport.Add("CO02;", "CO020000;");
        transport.Add("RT;", "RT1;");
        transport.Add("XT;", "XT0;");
        transport.Add("NB00;");
        transport.Add("ML0000;");
        transport.Add("PR01;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        bool[] values = [];
        var observed = new List<bool>();
        foreach (RadioSwitchId control in Enum.GetValues<RadioSwitchId>())
        {
            observed.Add((await driver.ReadSwitchAsync(control)).Enabled);
        }

        values = observed.ToArray();
        Assert.Equal([true, true, true, true, true, false, false, true, false, true, true, true, false, true, false], values);
        await driver.WriteSwitchAsync(RadioSwitchId.NoiseBlanker, false);
        await driver.WriteSwitchAsync(RadioSwitchId.Monitor, false);
        await driver.WriteSwitchAsync(RadioSwitchId.SpeechProcessor, true);
        Assert.Equal(15, driver.Capabilities.Switches.Count);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesTypedChoicesIncludingReadOnlyAgcStates()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("RA0;", "RA03;");
        transport.Add("PA0;", "PA02;");
        transport.Add("GT0;", "GT06;");
        transport.Add("RA01;");
        transport.Add("PA00;");
        transport.Add("GT04;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal("18db", (await driver.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
        Assert.Equal("amp2", (await driver.ReadChoiceAsync(RadioChoiceId.Preamp)).Value);
        Assert.Equal("auto-slow", (await driver.ReadChoiceAsync(RadioChoiceId.Agc)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.Attenuator, "6db");
        await driver.WriteChoiceAsync(RadioChoiceId.Preamp, "ipo");
        await driver.WriteChoiceAsync(RadioChoiceId.Agc, "auto");

        Assert.False(driver.Capabilities.Choices[RadioChoiceId.Agc].Options["auto-slow"].Writable);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => driver.WriteChoiceAsync(RadioChoiceId.Agc, "auto-slow").AsTask());
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesFilteringControlsWithEngineeringUnits()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("IS0;", "IS00-0120;");
        transport.Add("BP01;", "BP01123;");
        transport.Add("CO01;", "CO010850;");
        transport.Add("IS00+0400;");
        transport.Add("BP01075;");
        transport.Add("CO011200;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(-120, (await driver.ReadControlAsync(RadioControlId.IfShiftHz)).Value);
        Assert.Equal(1230, (await driver.ReadControlAsync(RadioControlId.ManualNotchFrequencyHz)).Value);
        Assert.Equal(850, (await driver.ReadControlAsync(RadioControlId.ContourFrequencyHz)).Value);
        await driver.WriteControlAsync(RadioControlId.IfShiftHz, 400);
        await driver.WriteControlAsync(RadioControlId.ManualNotchFrequencyHz, 750);
        await driver.WriteControlAsync(RadioControlId.ContourFrequencyHz, 1200);

        Assert.Equal(20, driver.Capabilities.Controls[RadioControlId.IfShiftHz].Step);
        Assert.Equal(10, driver.Capabilities.Controls[RadioControlId.ManualNotchFrequencyHz].Step);
        transport.AssertComplete();
    }

    [Fact]
    public async Task RoofingFilterUsesDistinctReadAndWriteCodes()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("RF0;", "RF07;");
        transport.Add("RF04;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal("3khz", (await driver.ReadChoiceAsync(RadioChoiceId.RoofingFilter)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.RoofingFilter, "500hz");

        transport.AssertComplete();
    }

    [Fact]
    public async Task FilterWidthIsDecodedAndEncodedUsingActiveMode()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("SH0;", "SH0013;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("SH0020;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal("2400hz", (await driver.ReadChoiceAsync(RadioChoiceId.FilterWidth)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.FilterWidth, "3000hz");

        RadioChoiceOption option = driver.Capabilities.Choices[RadioChoiceId.FilterWidth].Options["300hz"];
        Assert.Contains(RadioMode.Usb, option.ApplicableModes!);
        Assert.Contains(RadioMode.Cw, option.ApplicableModes!);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesSignedClarifierOffset()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("CF001;", "CF001-0150;");
        transport.Add("CF001+0200;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(-150, (await driver.ReadControlAsync(RadioControlId.ClarifierOffsetHz)).Value);
        await driver.WriteControlAsync(RadioControlId.ClarifierOffsetHz, 200);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ClarifierQueryIgnoresWrongSubcommandAndMalformedFrames()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("CF001;", "CF999+0001;CF001+99999;CF001+0123;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(123, (await driver.ReadControlAsync(RadioControlId.ClarifierOffsetHz)).Value);
        transport.AssertComplete();
    }

    [Fact]
    public async Task MeterQueryRejectsNonzeroReservedSuffix()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("RM3;", "RM3064123;RM3064000;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(64, (await driver.ReadMeterAsync(RadioMeterId.Compression)).RawValue);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesCwPitchInEngineeringUnits()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("KP;", "KP04;");
        transport.Add("KP05;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(340, (await driver.ReadControlAsync(RadioControlId.CwPitchHz)).Value);
        await driver.WriteControlAsync(RadioControlId.CwPitchHz, 350);

        NumericControlDescriptor descriptor = driver.Capabilities.Controls[RadioControlId.CwPitchHz];
        Assert.Equal(300, descriptor.Minimum);
        Assert.Equal(1050, descriptor.Maximum);
        Assert.Equal(10, descriptor.Step);
        Assert.Equal("Hz", descriptor.Unit);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesKeyerSpeedInWordsPerMinute()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("KS;", "KS020;");
        transport.Add("KS025;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(20, (await driver.ReadControlAsync(RadioControlId.KeyerSpeedWpm)).Value);
        await driver.WriteControlAsync(RadioControlId.KeyerSpeedWpm, 25);

        NumericControlDescriptor descriptor = driver.Capabilities.Controls[RadioControlId.KeyerSpeedWpm];
        Assert.Equal(4, descriptor.Minimum);
        Assert.Equal(60, descriptor.Maximum);
        Assert.Equal("WPM", descriptor.Unit);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesVoxDelayUsingDiscreteRadioCodes()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("VD;", "VD13;");
        transport.Add("VD04;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal("1000ms", (await driver.ReadChoiceAsync(RadioChoiceId.VoxDelay)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.VoxDelay, "200ms");

        Assert.Equal(31, driver.Capabilities.Choices[RadioChoiceId.VoxDelay].Options.Count);
        transport.AssertComplete();
    }

    [Fact]
    public async Task ReadsAndWritesAudioPeakFilterParameters()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("CO03;", "CO030030;");
        transport.Add("EX030201;", "EX0302011;");
        transport.Add("CO030020;");
        transport.Add("EX0302012;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal(50, (await driver.ReadControlAsync(RadioControlId.AudioPeakFilterOffsetHz)).Value);
        Assert.Equal("medium", (await driver.ReadChoiceAsync(RadioChoiceId.AudioPeakFilterWidth)).Value);
        await driver.WriteControlAsync(RadioControlId.AudioPeakFilterOffsetHz, -50);
        await driver.WriteChoiceAsync(RadioChoiceId.AudioPeakFilterWidth, "wide");

        transport.AssertComplete();
    }

    [Fact]
    public async Task TuningStepUsesModeAwareFastStepCommand()
    {
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("FS;", "FS1;");
        transport.Add("VS;", "VS0;");
        transport.Add("MD0;", "MD02;");
        transport.Add("FS0;");
        await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(transport);

        Assert.Equal("100hz", (await driver.ReadChoiceAsync(RadioChoiceId.TuningStep)).Value);
        await driver.WriteChoiceAsync(RadioChoiceId.TuningStep, "10hz");

        RadioChoiceOption option = driver.Capabilities.Choices[RadioChoiceId.TuningStep].Options["1khz"];
        Assert.Contains(RadioMode.Fm, option.ApplicableModes!);
        Assert.DoesNotContain(RadioMode.Usb, option.ApplicableModes!);
        transport.AssertComplete();
    }
}
