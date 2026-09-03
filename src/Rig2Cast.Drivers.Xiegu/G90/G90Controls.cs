using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Xiegu.Protocol;
using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Drivers.Xiegu.G90;

public sealed partial class G90Driver
{
    private static readonly Dictionary<RadioControlId, (byte Subcommand, bool Writable)> NumericCommands =
        new Dictionary<RadioControlId, (byte, bool)>
        {
            [RadioControlId.AfGain] = (0x01, false),
            [RadioControlId.RfGain] = (0x02, false),
            [RadioControlId.NoiseReductionLevel] = (0x06, false),
            [RadioControlId.TransmitPower] = (0x0A, false),
            [RadioControlId.MicrophoneGain] = (0x0B, false),
            [RadioControlId.NoiseBlankerLevel] = (0x12, false),
            [RadioControlId.MonitorLevel] = (0x15, false),
            [RadioControlId.AntiVoxLevel] = (0x17, false)
        };

    private static readonly IReadOnlyDictionary<RadioMeterId, byte> MeterCommands =
        new Dictionary<RadioMeterId, byte>
        {
            [RadioMeterId.SignalStrength] = 0x02,
            [RadioMeterId.Power] = 0x11,
            [RadioMeterId.Swr] = 0x12,
            [RadioMeterId.Alc] = 0x13
        };

    private static readonly Dictionary<RadioSwitchId, (byte Command, byte Subcommand, bool Writable)> SwitchCommands =
        new Dictionary<RadioSwitchId, (byte, byte, bool)>
        {
            [RadioSwitchId.NoiseBlanker] = (0x16, 0x22, true),
            [RadioSwitchId.SpeechProcessor] = (0x16, 0x44, true),
            [RadioSwitchId.AntennaTuner] = (0x1C, 0x01, true),
            [RadioSwitchId.DialLock] = (0x16, 0x50, false),
            [RadioSwitchId.ReceiveClarifier] = (0x21, 0x01, true),
            [RadioSwitchId.TransmitClarifier] = (0x21, 0x02, true)
        };

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioControlId.ClarifierOffsetHz)
            return new(control, await ReadClarifierOffsetAsync(cancellationToken).ConfigureAwait(false), _timeProvider.GetUtcNow());
        if (!NumericCommands.TryGetValue(control, out var wire))
            throw new NotSupportedException($"G90 numeric control '{control}' is not implemented.");
        int value = await ReadRawLevelAsync(0x14, wire.Subcommand, cancellationToken).ConfigureAwait(false);
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
        if (!NumericCommands.TryGetValue(control, out var wire))
            throw new NotSupportedException($"G90 numeric control '{control}' is not implemented.");
        if (!wire.Writable)
            throw new NotSupportedException($"G90 numeric control '{control}' is read-only.");
        if (value is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(value), value, "G90 level must be between 0 and 255.");
        byte[] encoded = CivBcd.EncodeBigEndian(value, 2);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x14, wire.Subcommand, .. encoded]),
            cancellationToken).ConfigureAwait(false);
        int readback = await ReadRawLevelAsync(0x14, wire.Subcommand, cancellationToken).ConfigureAwait(false);
        if (readback != value)
            throw new XieguProtocolException($"G90 {control} readback was {readback} after requesting {value}.");
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!MeterCommands.TryGetValue(meter, out byte subcommand))
            throw new NotSupportedException($"G90 meter '{meter}' is not implemented.");
        int raw = await ReadRawLevelAsync(0x15, subcommand, cancellationToken).ConfigureAwait(false);
        return new(meter, raw, raw / 1023d, _timeProvider.GetUtcNow());
    }

    public async ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!SwitchCommands.TryGetValue(control, out var wire))
            throw new NotSupportedException($"G90 switch '{control}' is not implemented.");
        CivFrame response = await QueryAsync(
            [wire.Command, wire.Subcommand], new byte[] { wire.Command, wire.Subcommand }, cancellationToken)
            .ConfigureAwait(false);
        bool enabled = control == RadioSwitchId.AntennaTuner
            ? ParseTunerState(response)
            : ParseBoolean(response, wire.Command, 2);
        return new(control, enabled, _timeProvider.GetUtcNow());
    }

    public async ValueTask WriteSwitchAsync(
        RadioSwitchId control, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!SwitchCommands.TryGetValue(control, out var wire))
            throw new NotSupportedException($"G90 switch '{control}' is not implemented.");
        if (!wire.Writable)
            throw new NotSupportedException($"G90 switch '{control}' is read-only.");
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress,
                [wire.Command, wire.Subcommand, enabled ? (byte)1 : (byte)0]), cancellationToken).ConfigureAwait(false);
        bool readback = (await ReadSwitchAsync(control, cancellationToken).ConfigureAwait(false)).Enabled;
        if (readback != enabled)
            throw new XieguProtocolException($"G90 {control} readback was {readback} after requesting {enabled}.");
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioChoiceId.Attenuator)
            return new(control, await ReadAttenuatorAsync(cancellationToken).ConfigureAwait(false), _timeProvider.GetUtcNow());
        byte subcommand = ChoiceSubcommand(control);
        CivFrame response = await QueryAsync(
            [0x16, subcommand], new byte[] { 0x16, subcommand }, cancellationToken).ConfigureAwait(false);
        if (response.Message.Length != 3)
            throw new XieguProtocolException($"Invalid G90 {control} response {FormatFrame(response)}.");
        return new(control, DecodeChoice(control, response.Message.Span[2]), _timeProvider.GetUtcNow());
    }

    public async ValueTask WriteChoiceAsync(
        RadioChoiceId control, string value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioChoiceId.Attenuator)
        {
            await WriteAttenuatorAsync(value, cancellationToken).ConfigureAwait(false);
            return;
        }
        byte encoded = EncodeChoice(control, value);
        byte subcommand = ChoiceSubcommand(control);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x16, subcommand, encoded]),
            cancellationToken).ConfigureAwait(false);
        string readback = (await ReadChoiceAsync(control, cancellationToken).ConfigureAwait(false)).Value;
        if (!StringComparer.OrdinalIgnoreCase.Equals(readback, value))
            throw new XieguProtocolException($"G90 {control} readback was '{readback}' after requesting '{value}'.");
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

    private async ValueTask<int> ReadRawLevelAsync(
        byte command, byte subcommand, CancellationToken cancellationToken)
    {
        CivFrame frame = await QueryAsync(
            [command, subcommand], new byte[] { command, subcommand }, cancellationToken).ConfigureAwait(false);
        if (frame.Message.Length != 4)
            throw new XieguProtocolException($"Invalid G90 level response {FormatFrame(frame)}.");
        int value = (frame.Message.Span[2] << 8) | frame.Message.Span[3];
        int maximum = command == 0x14 ? 607 : 1_023;
        if (value > maximum)
            throw new XieguProtocolException(
                $"G90 response exceeded the observed {maximum} raw maximum: {FormatFrame(frame)}.");
        return value;
    }

    private async ValueTask<int> ReadClarifierOffsetAsync(CancellationToken cancellationToken)
    {
        CivFrame frame = await QueryAsync(
            [0x21, 0x00], new byte[] { 0x21, 0x00 }, cancellationToken).ConfigureAwait(false);
        if (frame.Message.Length != 5 ||
            !CivBcd.TryDecode(frame.Message.Span[2..4], out long magnitude) || magnitude > 9_999 ||
            frame.Message.Span[4] is not (0x00 or 0x01))
            throw new XieguProtocolException($"Invalid G90 RIT response {FormatFrame(frame)}.");
        return (int)(frame.Message.Span[4] == 1 ? -magnitude : magnitude);
    }

    private async ValueTask WriteClarifierOffsetAsync(int value, CancellationToken cancellationToken)
    {
        if (value is < -9_999 or > 9_999)
            throw new ArgumentOutOfRangeException(nameof(value), value, "G90 RIT offset must be -9999 to +9999 Hz.");
        byte[] magnitude = CivBcd.Encode(Math.Abs((long)value), 2);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress,
                [0x21, 0x00, .. magnitude, value < 0 ? (byte)1 : (byte)0]), cancellationToken).ConfigureAwait(false);
        int readback = await ReadClarifierOffsetAsync(cancellationToken).ConfigureAwait(false);
        if (readback != value)
            throw new XieguProtocolException($"G90 RIT readback was {readback} Hz after requesting {value} Hz.");
    }

    private static byte ChoiceSubcommand(RadioChoiceId control) => control switch
    {
        RadioChoiceId.Preamp => 0x02,
        RadioChoiceId.Agc => 0x12,
        _ => throw new NotSupportedException($"G90 choice '{control}' is not implemented.")
    };

    private async ValueTask<string> ReadAttenuatorAsync(CancellationToken cancellationToken)
    {
        CivFrame response = await QueryAsync([0x11], new byte[] { 0x11 }, cancellationToken).ConfigureAwait(false);
        if (response.Message.Length != 2)
            throw new XieguProtocolException($"Invalid G90 attenuator response {FormatFrame(response)}.");
        // Firmware 1.81 reports 0C when its attenuator is active. Preserve the public
        // binary choice semantics: zero is off and a nonzero attenuation value is on.
        return response.Message.Span[1] == 0x00 ? "off" : "on";
    }

    private async ValueTask WriteAttenuatorAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        bool requested = value.ToLowerInvariant() switch
        {
            "off" => false,
            "on" => true,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Value is not valid for G90 Attenuator.")
        };
        bool current = StringComparer.OrdinalIgnoreCase.Equals(
            await ReadAttenuatorAsync(cancellationToken).ConfigureAwait(false), "on");
        if (current != requested)
        {
            // The G90 command is a toggle and physical firmware 1.81 applies it without
            // returning FB. Treat the subsequent state query as the confirmation.
            await _session.SendAsync(
                new CivFrame(_radioAddress, _controllerAddress, [0x11, 0x00]),
                cancellationToken).ConfigureAwait(false);
        }
        string readback = await ReadAttenuatorAsync(cancellationToken).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(readback, value))
            throw new XieguProtocolException($"G90 Attenuator readback was '{readback}' after requesting '{value}'.");
    }

    private static bool ParseTunerState(CivFrame frame)
    {
        if (frame.Message.Length != 3 || frame.Message.Span[0] != 0x1C ||
            frame.Message.Span[1] != 0x01 || frame.Message.Span[2] > 0x02)
            throw new XieguProtocolException($"Invalid G90 tuner response {FormatFrame(frame)}.");
        return frame.Message.Span[2] != 0x00;
    }

    private static string DecodeChoice(RadioChoiceId control, byte value) => (control, value) switch
    {
        (RadioChoiceId.Preamp, 0x00) => "off",
        (RadioChoiceId.Preamp, 0x01 or 0x02) => "on",
        (RadioChoiceId.Agc, 0x00) => "off",
        (RadioChoiceId.Agc, 0x01) => "fast",
        (RadioChoiceId.Agc, 0x02) => "slow",
        (RadioChoiceId.Agc, 0x03) => "auto",
        _ => throw new XieguProtocolException($"Invalid G90 {control} value {value:X2}.")
    };

    private static byte EncodeChoice(RadioChoiceId control, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return (control, value.ToLowerInvariant()) switch
        {
            (RadioChoiceId.Preamp, "off") => 0x00,
            (RadioChoiceId.Preamp, "on") => 0x01,
        (RadioChoiceId.Agc, "off") => 0x00,
        (RadioChoiceId.Agc, "fast") => 0x01,
        (RadioChoiceId.Agc, "slow") => 0x02,
        (RadioChoiceId.Agc, "auto") => 0x03,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, $"Value is not valid for G90 {control}.")
        };
    }

    private static Dictionary<RadioControlId, NumericControlDescriptor> CreateControls(
        FeatureDescriptor readOnly, FeatureDescriptor readWrite)
    {
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        var controls = NumericCommands.ToDictionary(
            item => item.Key,
            item => new NumericControlDescriptor(item.Key, item.Key.ToString(),
                item.Value.Writable ? readWrite : readOnly, 0, 607, 1, "G90 binary raw")
            { ReceiverTargets = main });
        controls[RadioControlId.ClarifierOffsetHz] = new(
            RadioControlId.ClarifierOffsetHz, "RIT offset", readWrite, -9_999, 9_999, 1, "Hz")
        { ReceiverTargets = main };
        return controls;
    }

    private static Dictionary<RadioSwitchId, SwitchControlDescriptor> CreateSwitches(
        FeatureDescriptor readOnly, FeatureDescriptor readWrite)
    {
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        return SwitchCommands.ToDictionary(item => item.Key,
            item => new SwitchControlDescriptor(item.Key, item.Key.ToString(),
                item.Value.Writable ? readWrite : readOnly) { ReceiverTargets = main });
    }

    private static Dictionary<RadioChoiceId, ChoiceControlDescriptor> CreateChoices(FeatureDescriptor readWrite)
    {
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        static IReadOnlyDictionary<string, RadioChoiceOption> Options(params (string Value, string Name)[] values) =>
            values.ToDictionary(item => item.Value, item => new RadioChoiceOption(item.Value, item.Name));
        return new Dictionary<RadioChoiceId, ChoiceControlDescriptor>
        {
            [RadioChoiceId.Attenuator] = new(RadioChoiceId.Attenuator, "Attenuator", readWrite,
                Options(("off", "Off"), ("on", "On"))) { ReceiverTargets = main },
            [RadioChoiceId.Preamp] = new(RadioChoiceId.Preamp, "Preamp", readWrite,
                Options(("off", "Off"), ("on", "On"))) { ReceiverTargets = main },
            [RadioChoiceId.Agc] = new(RadioChoiceId.Agc, "AGC", readWrite,
                Options(("off", "Off"), ("fast", "Fast"), ("slow", "Slow"), ("auto", "Auto")))
            { ReceiverTargets = main }
        };
    }

    private static Dictionary<RadioMeterId, RadioMeterDescriptor> CreateMeters()
    {
        var mainRange = new Dictionary<ReceiverId, RadioMeterRange>
        {
            [ReceiverId.Main] = new(0, 1_023, "G90 binary raw")
        };
        return MeterCommands.Keys.ToDictionary(id => id, id => new RadioMeterDescriptor(
            id, id.ToString(), 0, 1_023, "G90 binary raw", false)
        {
            RequiresTransmit = id is RadioMeterId.Power or RadioMeterId.Swr or RadioMeterId.Alc,
            RangesByReceiver = mainRange
        });
    }
}
