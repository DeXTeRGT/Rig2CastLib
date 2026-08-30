using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Drivers.Yaesu.Protocol;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;

namespace Rig2Cast.Runtime.Tests;

public sealed class Ftdx10DriverTests
{
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
        Assert.False(state.IsTransmitting);
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

        Assert.Equal(15, driver.Capabilities.Controls.Count);
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
        Assert.All(driver.Capabilities.Meters.Values, descriptor => Assert.False(descriptor.CalibrationAvailable));
        transport.AssertComplete();
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
}
