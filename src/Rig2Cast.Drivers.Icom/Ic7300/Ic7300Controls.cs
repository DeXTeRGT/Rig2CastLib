using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Icom.Protocol;
using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Drivers.Icom.Ic7300;

public sealed partial class Ic7300Driver
{
    private static readonly Dictionary<RadioControlId, byte> NumericCommands =
        new Dictionary<RadioControlId, byte>
        {
            [RadioControlId.AfGain] = 0x01,
            [RadioControlId.RfGain] = 0x02,
            [RadioControlId.Squelch] = 0x03,
            [RadioControlId.TransmitPower] = 0x0A,
            [RadioControlId.NoiseReductionLevel] = 0x06,
            [RadioControlId.NoiseBlankerLevel] = 0x12
        };

    private static readonly IReadOnlyDictionary<RadioMeterId, byte> MeterCommands =
        new Dictionary<RadioMeterId, byte>
        {
            [RadioMeterId.SignalStrength] = 0x02,
            [RadioMeterId.Power] = 0x11,
            [RadioMeterId.Swr] = 0x12,
            [RadioMeterId.Alc] = 0x13
        };

    private static readonly IReadOnlyDictionary<RadioSwitchId, (byte Command, byte Subcommand)> SwitchCommands =
        new Dictionary<RadioSwitchId, (byte, byte)>
        {
            [RadioSwitchId.NoiseBlanker] = (0x16, 0x22),
            [RadioSwitchId.NoiseReduction] = (0x16, 0x40),
            [RadioSwitchId.AutoNotch] = (0x16, 0x41),
            [RadioSwitchId.ManualNotch] = (0x16, 0x48),
            [RadioSwitchId.ReceiveClarifier] = (0x21, 0x01),
            [RadioSwitchId.TransmitClarifier] = (0x21, 0x02)
        };

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioControlId.ClarifierOffsetHz)
            return new(control, await ReadClarifierOffsetAsync(cancellationToken).ConfigureAwait(false), _timeProvider.GetUtcNow());
        if (!NumericCommands.TryGetValue(control, out byte subcommand))
            throw new NotSupportedException($"IC-7300 numeric control '{control}' is not implemented.");
        int value = await ReadBcdLevelAsync(0x14, subcommand, cancellationToken).ConfigureAwait(false);
        return new(control, value, _timeProvider.GetUtcNow());
    }

    public async ValueTask WriteControlAsync(
        RadioControlId control, int value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioControlId.ClarifierOffsetHz)
        {
            await WriteClarifierOffsetAsync(value, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!NumericCommands.TryGetValue(control, out byte subcommand))
            throw new NotSupportedException($"IC-7300 numeric control '{control}' is not implemented.");
        if (value is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(value), value, "IC-7300 level must be between 0 and 255.");
        byte[] encoded = CivBcd.EncodeBigEndian(value, 2);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x14, subcommand, .. encoded]),
            cancellationToken).ConfigureAwait(false);
        int readback = await ReadBcdLevelAsync(0x14, subcommand, cancellationToken).ConfigureAwait(false);
        if (readback != value)
            throw new IcomProtocolException($"IC-7300 {control} readback was {readback} after requesting {value}.");
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!MeterCommands.TryGetValue(meter, out byte subcommand))
            throw new NotSupportedException($"IC-7300 meter '{meter}' is not implemented.");
        int raw = await ReadBcdLevelAsync(0x15, subcommand, cancellationToken).ConfigureAwait(false);
        return new(meter, raw, raw / 255d, _timeProvider.GetUtcNow());
    }

    public async ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!SwitchCommands.TryGetValue(control, out var wire))
            throw new NotSupportedException($"IC-7300 switch '{control}' is not implemented.");
        bool enabled = ParseBoolean(await QueryAsync(
            [wire.Command, wire.Subcommand], new byte[] { wire.Command, wire.Subcommand }, cancellationToken)
            .ConfigureAwait(false), wire.Command, 2);
        return new(control, enabled, _timeProvider.GetUtcNow());
    }

    public async ValueTask WriteSwitchAsync(
        RadioSwitchId control, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!SwitchCommands.TryGetValue(control, out var wire))
            throw new NotSupportedException($"IC-7300 switch '{control}' is not implemented.");
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress,
                [wire.Command, wire.Subcommand, enabled ? (byte)0x01 : (byte)0x00]),
            cancellationToken).ConfigureAwait(false);
        bool readback = (await ReadSwitchAsync(control, cancellationToken).ConfigureAwait(false)).Enabled;
        if (readback != enabled)
            throw new IcomProtocolException($"IC-7300 {control} readback was {readback} after requesting {enabled}.");
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        (byte command, byte? subcommand) = GetChoiceWire(control);
        byte[] query = subcommand is byte sub ? [command, sub] : [command];
        CivFrame response = await QueryAsync(query, query, cancellationToken).ConfigureAwait(false);
        int valueOffset = query.Length;
        if (response.Message.Length != valueOffset + 1)
            throw new IcomProtocolException($"Invalid IC-7300 {control} response {FormatFrame(response)}.");
        string value = DecodeChoice(control, response.Message.Span[valueOffset]);
        return new(control, value, _timeProvider.GetUtcNow());
    }

    public async ValueTask WriteChoiceAsync(
        RadioChoiceId control, string value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        byte encoded = EncodeChoice(control, value);
        (byte command, byte? subcommand) = GetChoiceWire(control);
        byte[] message = subcommand is byte sub ? [command, sub, encoded] : [command, encoded];
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, message), cancellationToken).ConfigureAwait(false);
        RadioChoiceValue readback = await ReadChoiceAsync(control, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(readback.Value, value))
            throw new IcomProtocolException($"IC-7300 {control} readback was '{readback.Value}' after requesting '{value}'.");
    }

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return (await ReadControlAsync(control, cancellationToken).ConfigureAwait(false)) with { Receiver = receiver };
    }

    public ValueTask WriteControlAsync(
        RadioControlId control, ReceiverId receiver, int value, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return WriteControlAsync(control, value, cancellationToken);
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return (await ReadMeterAsync(meter, cancellationToken).ConfigureAwait(false)) with { Receiver = receiver };
    }

    public async ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return (await ReadSwitchAsync(control, cancellationToken).ConfigureAwait(false)) with { Receiver = receiver };
    }

    public ValueTask WriteSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return WriteSwitchAsync(control, enabled, cancellationToken);
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return (await ReadChoiceAsync(control, cancellationToken).ConfigureAwait(false)) with { Receiver = receiver };
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, string value, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return WriteChoiceAsync(control, value, cancellationToken);
    }

    private async ValueTask<int> ReadBcdLevelAsync(
        byte command, byte subcommand, CancellationToken cancellationToken)
    {
        CivFrame frame = await QueryAsync(
            [command, subcommand], new byte[] { command, subcommand }, cancellationToken).ConfigureAwait(false);
        if (frame.Message.Length != 4 || !CivBcd.TryDecodeBigEndian(frame.Message.Span[2..], out long value) || value > 255)
            throw new IcomProtocolException($"Invalid IC-7300 level response {FormatFrame(frame)}.");
        return (int)value;
    }

    private async ValueTask<int> ReadClarifierOffsetAsync(CancellationToken cancellationToken)
    {
        CivFrame frame = await QueryAsync(
            [0x21, 0x00], new byte[] { 0x21, 0x00 }, cancellationToken).ConfigureAwait(false);
        if (frame.Message.Length != 5 || !CivBcd.TryDecode(frame.Message.Span[2..4], out long magnitude) ||
            magnitude > 9_999 || frame.Message.Span[4] is not (0x00 or 0x01))
            throw new IcomProtocolException($"Invalid IC-7300 RIT/Delta-TX response {FormatFrame(frame)}.");
        return (int)(frame.Message.Span[4] == 0x01 ? -magnitude : magnitude);
    }

    private async ValueTask WriteClarifierOffsetAsync(int value, CancellationToken cancellationToken)
    {
        if (value is < -9_999 or > 9_999)
            throw new ArgumentOutOfRangeException(nameof(value), value, "IC-7300 RIT/Delta-TX offset must be -9999 to +9999 Hz.");
        byte[] magnitude = CivBcd.Encode(Math.Abs((long)value), 2);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress,
                [0x21, 0x00, .. magnitude, value < 0 ? (byte)0x01 : (byte)0x00]),
            cancellationToken).ConfigureAwait(false);
        int readback = await ReadClarifierOffsetAsync(cancellationToken).ConfigureAwait(false);
        if (readback != value)
            throw new IcomProtocolException($"IC-7300 clarifier readback was {readback} Hz after requesting {value} Hz.");
    }

    private static (byte Command, byte? Subcommand) GetChoiceWire(RadioChoiceId control) => control switch
    {
        RadioChoiceId.Attenuator => (0x11, null),
        RadioChoiceId.Preamp => (0x16, 0x02),
        RadioChoiceId.Agc => (0x16, 0x12),
        _ => throw new NotSupportedException($"IC-7300 choice '{control}' is not implemented.")
    };

    private static string DecodeChoice(RadioChoiceId control, byte value) => (control, value) switch
    {
        (RadioChoiceId.Attenuator, 0x00) => "off",
        (RadioChoiceId.Attenuator, 0x20) => "20db",
        (RadioChoiceId.Preamp, 0x00) => "off",
        (RadioChoiceId.Preamp, 0x01) => "preamp1",
        (RadioChoiceId.Preamp, 0x02) => "preamp2",
        (RadioChoiceId.Agc, 0x01) => "fast",
        (RadioChoiceId.Agc, 0x02) => "medium",
        (RadioChoiceId.Agc, 0x03) => "slow",
        _ => throw new IcomProtocolException($"Invalid IC-7300 {control} value {value:X2}.")
    };

    private static byte EncodeChoice(RadioChoiceId control, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return (control, value.ToLowerInvariant()) switch
        {
            (RadioChoiceId.Attenuator, "off") => 0x00,
            (RadioChoiceId.Attenuator, "20db") => 0x20,
            (RadioChoiceId.Preamp, "off") => 0x00,
            (RadioChoiceId.Preamp, "preamp1") => 0x01,
            (RadioChoiceId.Preamp, "preamp2") => 0x02,
            (RadioChoiceId.Agc, "fast") => 0x01,
            (RadioChoiceId.Agc, "medium") => 0x02,
            (RadioChoiceId.Agc, "slow") => 0x03,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, $"Value is not valid for IC-7300 {control}.")
        };
    }

    private static Dictionary<RadioControlId, NumericControlDescriptor> CreateControls(FeatureDescriptor feature)
    {
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        var controls = new Dictionary<RadioControlId, NumericControlDescriptor>();
        foreach ((RadioControlId id, string name) in new[]
                 {
                     (RadioControlId.AfGain, "AF gain"), (RadioControlId.RfGain, "RF gain"),
                     (RadioControlId.Squelch, "Squelch"), (RadioControlId.TransmitPower, "Transmit power"),
                     (RadioControlId.NoiseReductionLevel, "Noise reduction level"),
                     (RadioControlId.NoiseBlankerLevel, "Noise blanker level")
                 })
            controls[id] = new(id, name, feature, 0, 255, 1, "raw") { ReceiverTargets = main };
        controls[RadioControlId.ClarifierOffsetHz] = new(
            RadioControlId.ClarifierOffsetHz, "RIT/Delta-TX offset", feature, -9_999, 9_999, 1, "Hz")
        { ReceiverTargets = main };
        return controls;
    }

    private static Dictionary<RadioSwitchId, SwitchControlDescriptor> CreateSwitches(FeatureDescriptor feature)
    {
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        return SwitchCommands.Keys.ToDictionary(
            id => id, id => new SwitchControlDescriptor(id, id.ToString(), feature) { ReceiverTargets = main });
    }

    private static Dictionary<RadioChoiceId, ChoiceControlDescriptor> CreateChoices(FeatureDescriptor feature)
    {
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        static IReadOnlyDictionary<string, RadioChoiceOption> Options(params (string Value, string Name)[] values) =>
            values.ToDictionary(value => value.Value, value => new RadioChoiceOption(value.Value, value.Name));
        return new Dictionary<RadioChoiceId, ChoiceControlDescriptor>
        {
            [RadioChoiceId.Attenuator] = new(RadioChoiceId.Attenuator, "Attenuator", feature,
                Options(("off", "Off"), ("20db", "20 dB"))) { ReceiverTargets = main },
            [RadioChoiceId.Preamp] = new(RadioChoiceId.Preamp, "Preamp", feature,
                Options(("off", "Off"), ("preamp1", "Preamp 1"), ("preamp2", "Preamp 2"))) { ReceiverTargets = main },
            [RadioChoiceId.Agc] = new(RadioChoiceId.Agc, "AGC", feature,
                Options(("fast", "Fast"), ("medium", "Medium"), ("slow", "Slow"))) { ReceiverTargets = main }
        };
    }

    private static Dictionary<RadioMeterId, RadioMeterDescriptor> CreateMeters()
    {
        var mainRange = new Dictionary<ReceiverId, RadioMeterRange>
        {
            [ReceiverId.Main] = new(0, 255, "CI-V raw")
        };
        return MeterCommands.Keys.ToDictionary(id => id, id => new RadioMeterDescriptor(
            id, id.ToString(), 0, 255, "CI-V raw", false)
        {
            RequiresTransmit = id is RadioMeterId.Power or RadioMeterId.Swr or RadioMeterId.Alc,
            RangesByReceiver = mainRange
        });
    }
}
