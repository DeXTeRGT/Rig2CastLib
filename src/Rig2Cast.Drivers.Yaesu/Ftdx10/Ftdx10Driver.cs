using System.Globalization;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Drivers.Yaesu.Protocol;

namespace Rig2Cast.Drivers.Yaesu.Ftdx10;

public sealed class Ftdx10Driver : IRadioDriver, IRadioControlDriver, IRadioMeterDriver, IRadioSwitchDriver, IRadioChoiceDriver, IRadioObservationSource
{
    private static readonly Dictionary<RadioSwitchId, SwitchCommand> SwitchCommands = new()
    {
        [RadioSwitchId.NoiseBlanker] = new("Noise blanker", "NB0", "NB0", "NB0", '0', '1'),
        [RadioSwitchId.NoiseReduction] = new("Noise reduction", "NR0", "NR0", "NR0", '0', '1'),
        [RadioSwitchId.Monitor] = new("Monitor", "ML0", "ML0", "ML0", '0', '1', 3),
        [RadioSwitchId.SpeechProcessor] = new("Speech processor", "PR0", "PR0", "PR0", '0', '1'),
        [RadioSwitchId.Vox] = new("VOX", "VX", "VX", "VX", '0', '1'),
        [RadioSwitchId.DialLock] = new("Main dial lock", "LK", "LK", "LK", '0', '1'),
        [RadioSwitchId.BreakIn] = new("CW break-in", "BI", "BI", "BI", '0', '1'),
        [RadioSwitchId.AntennaTuner] = new("Antenna tuner", "AC", "AC00", "AC00", '0', '1'),
        [RadioSwitchId.NarrowFilter] = new("Narrow filter", "NA0", "NA0", "NA0", '0', '1'),
        [RadioSwitchId.AutoNotch] = new("Auto notch", "BC0", "BC0", "BC0", '0', '1'),
        [RadioSwitchId.ManualNotch] = new("Manual notch", "BP00", "BP00", "BP00", '0', '1', 3),
        [RadioSwitchId.Contour] = new("Contour", "CO00", "CO00", "CO00", '0', '1', 4),
        [RadioSwitchId.AudioPeakFilter] = new("Audio peak filter", "CO02", "CO02", "CO02", '0', '1', 4),
        [RadioSwitchId.ReceiveClarifier] = new("Receive clarifier (RIT)", "RT", "RT", "RT", '0', '1'),
        [RadioSwitchId.TransmitClarifier] = new("Transmit clarifier (XIT)", "XT", "XT", "XT", '0', '1')
    };

    private static readonly Dictionary<RadioChoiceId, ChoiceCommand> ChoiceCommands = new()
    {
        [RadioChoiceId.Attenuator] = new("RF attenuator", "RA0", "RA0", new Dictionary<string, ChoiceCode>
        {
            ["off"] = new('0', "Off"), ["6db"] = new('1', "6 dB"),
            ["12db"] = new('2', "12 dB"), ["18db"] = new('3', "18 dB")
        }),
        [RadioChoiceId.Preamp] = new("Preamp (IPO)", "PA0", "PA0", new Dictionary<string, ChoiceCode>
        {
            ["ipo"] = new('0', "IPO"), ["amp1"] = new('1', "AMP 1"), ["amp2"] = new('2', "AMP 2")
        }),
        [RadioChoiceId.Agc] = new("AGC", "GT0", "GT0", new Dictionary<string, ChoiceCode>
        {
            ["off"] = new('0', "Off"), ["fast"] = new('1', "Fast"), ["mid"] = new('2', "Mid"),
            ["slow"] = new('3', "Slow"), ["auto"] = new('4', "Auto"),
            ["auto-fast"] = new('4', "Auto (Fast)", false),
            ["auto-mid"] = new('5', "Auto (Mid)", false),
            ["auto-slow"] = new('6', "Auto (Slow)", false)
        }),
        [RadioChoiceId.RoofingFilter] = new("Roofing filter", "RF0", "RF0", new Dictionary<string, ChoiceCode>
        {
            ["12khz"] = new('1', "12 kHz", true, '6'),
            ["3khz"] = new('2', "3 kHz", true, '7'),
            ["500hz"] = new('4', "500 Hz", true, '9'),
            ["300hz"] = new('5', "300 Hz (optional)", true, 'A')
        }),
        [RadioChoiceId.AudioPeakFilterWidth] = new("Audio peak filter width", "EX030201", "EX030201", new Dictionary<string, ChoiceCode>
        {
            ["narrow"] = new('0', "Narrow"), ["medium"] = new('1', "Medium"), ["wide"] = new('2', "Wide")
        })
    };

    private static readonly Dictionary<RadioControlId, ControlCommand> ControlCommands =
        new Dictionary<RadioControlId, ControlCommand>
        {
            [RadioControlId.AfGain] = new("AF gain", "AG0", "AG0", 3, 0, 255, "raw"),
            [RadioControlId.RfGain] = new("RF gain", "RG0", "RG0", 3, 0, 255, "raw"),
            [RadioControlId.Squelch] = new("Squelch", "SQ0", "SQ0", 3, 0, 100, "%"),
            [RadioControlId.MicrophoneGain] = new("Microphone gain", "MG", "MG", 3, 0, 100, "%"),
            [RadioControlId.TransmitPower] = new("Transmit power", "PC", "PC", 3, 5, 100, "W"),
            [RadioControlId.SpeechProcessorLevel] = new("Speech processor level", "PL", "PL", 3, 0, 100, "%"),
            [RadioControlId.NoiseReductionLevel] = new("Noise reduction level", "RL0", "RL0", 2, 1, 15, "step"),
            [RadioControlId.NoiseBlankerLevel] = new("Noise blanker level", "NL0", "NL0", 3, 0, 10, "step"),
            [RadioControlId.MonitorLevel] = new("Monitor level", "ML1", "ML1", 3, 0, 100, "%"),
            [RadioControlId.VoxGain] = new("VOX gain", "VG", "VG", 3, 0, 100, "%"),
            [RadioControlId.AntiVoxLevel] = new("Anti-VOX level", "AV", "AV", 3, 1, 100, "%"),
            [RadioControlId.ManualNotchFrequencyHz] = new("Manual notch frequency", "BP01", "BP01", 3, 10, 3200, "Hz", 10),
            [RadioControlId.ContourFrequencyHz] = new("Contour frequency", "CO01", "CO01", 4, 10, 3200, "Hz"),
            [RadioControlId.IfShiftHz] = new("IF shift", "IS0", "IS00", 5, -1200, 1200, "Hz", 20),
            [RadioControlId.ClarifierOffsetHz] = new("Clarifier offset", "CF001", "CF001", 5, -9999, 9999, "Hz"),
            [RadioControlId.CwPitchHz] = new("CW pitch", "KP", "KP", 2, 300, 1050, "Hz", 10, 300),
            [RadioControlId.KeyerSpeedWpm] = new("Keyer speed", "KS", "KS", 3, 4, 60, "WPM"),
            [RadioControlId.AudioPeakFilterOffsetHz] = new("APF offset", "CO03", "CO03", 4, -250, 250, "Hz", 10, -250)
        };

    private static readonly Dictionary<RadioMeterId, MeterCommand> MeterCommands =
        new Dictionary<RadioMeterId, MeterCommand>
        {
            [RadioMeterId.SignalStrength] = new("Signal strength", "SM0", "SM0", 7),
            [RadioMeterId.Compression] = new("Compression", "RM3", "RM3", 10),
            [RadioMeterId.Alc] = new("ALC", "RM4", "RM4", 10),
            [RadioMeterId.Power] = new("Power output", "RM5", "RM5", 10),
            [RadioMeterId.Swr] = new("SWR", "RM6", "RM6", 10),
            [RadioMeterId.DrainCurrent] = new("Drain current", "RM7", "RM7", 10),
            [RadioMeterId.DrainVoltage] = new("Drain voltage", "RM8", "RM8", 10)
        };
    private readonly IRadioTransport _transport;
    private readonly YaesuAsciiProtocol _protocol;
    private readonly bool _automaticInformationEnabled;
    private bool _disposed;

    private Ftdx10Driver(
        IRadioTransport transport,
        YaesuAsciiProtocol protocol,
        bool automaticInformationEnabled)
    {
        _transport = transport;
        _protocol = protocol;
        _automaticInformationEnabled = automaticInformationEnabled;
        Capabilities = CreateCapabilities();
    }

    public RadioCapabilities Capabilities { get; }

    public static async ValueTask<Ftdx10Driver> OpenAsync(
        IRadioTransport transport,
        TimeSpan? responseTimeout = null,
        bool enableAutomaticInformation = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var protocol = new YaesuAsciiProtocol(transport, responseTimeout);
        try
        {
            string identification = await protocol.QueryAsync("ID", "ID", cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(identification, $"ID{Ftdx10CatProfile.Identification};"))
            {
                throw new YaesuProtocolException(
                    $"Expected FTDX10 identification ID{Ftdx10CatProfile.Identification}; but received '{identification}'.");
            }

            if (enableAutomaticInformation)
            {
                await protocol.SendAsync("AI1", cancellationToken).ConfigureAwait(false);
                string automaticInformation = await protocol.QueryAsync("AI", "AI", cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(automaticInformation, "AI1;"))
                {
                    throw new YaesuProtocolException(
                        $"The FTDX10 did not confirm automatic information mode: '{automaticInformation}'.");
                }
            }

            return new Ftdx10Driver(transport, protocol, enableAutomaticInformation);
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await protocol.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public async ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        long frequencyA = ParseFrequency(await _protocol.QueryAsync("FA", "FA", cancellationToken).ConfigureAwait(false));
        long frequencyB = ParseFrequency(await _protocol.QueryAsync("FB", "FB", cancellationToken).ConfigureAwait(false));
        VfoId activeVfo = ParseVfo(await _protocol.QueryAsync("VS", "VS", cancellationToken).ConfigureAwait(false));
        RadioMode mode = ParseMode(await _protocol.QueryAsync(activeVfo == VfoId.A ? "MD0" : "MD1", "MD", cancellationToken).ConfigureAwait(false));
        bool split = ParseBoolean(await _protocol.QueryAsync("ST", "ST", cancellationToken).ConfigureAwait(false));
        bool transmitting = ParseTransmit(await _protocol.QueryAsync("TX", "TX", cancellationToken).ConfigureAwait(false));

        return new RadioState(
            1,
            ConnectionStatus.Connected,
            new Dictionary<VfoId, long> { [VfoId.A] = frequencyA, [VfoId.B] = frequencyB },
            activeVfo,
            mode,
            split,
            transmitting,
            DateTimeOffset.UtcNow);
    }

    public async ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfLessThan(frequencyHz, 30_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frequencyHz, 75_000_000);
        VfoId resolved = target == VfoId.Current ?
            ParseVfo(await _protocol.QueryAsync("VS", "VS", cancellationToken).ConfigureAwait(false)) : target;
        string prefix = resolved switch
        {
            VfoId.A => "FA",
            VfoId.B => "FB",
            _ => throw new NotSupportedException($"FTDX10 frequency targeting does not support VFO '{target}'.")
        };
        await _protocol.SendAsync($"{prefix}{frequencyHz:000000000}", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        VfoId active = ParseVfo(await _protocol.QueryAsync("VS", "VS", cancellationToken).ConfigureAwait(false));
        char target = active == VfoId.A ? '0' : '1';
        await _protocol.SendAsync($"MD{target}{Ftdx10CatProfile.EncodeMode(mode)}", cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return vfo switch
        {
            VfoId.A => _protocol.SendAsync("VS0", cancellationToken),
            VfoId.B => _protocol.SendAsync("VS1", cancellationToken),
            _ => throw new NotSupportedException($"FTDX10 active-VFO selection does not support '{vfo}'.")
        };
    }

    public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return _protocol.SendAsync(enabled ? "ST1" : "ST0", cancellationToken);
    }

    public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return _protocol.SendAsync(enabled ? "TX1" : "TX0", cancellationToken);
    }

    public async IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await foreach (string frame in _protocol.WatchUnsolicitedFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ParseObservation(frame);
        }
    }

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ControlCommand command = GetControlCommand(control);
        if (control == RadioControlId.IfShiftHz)
        {
            string shiftResponse = await _protocol.QueryAsync("IS0", "IS", cancellationToken).ConfigureAwait(false);
            if (shiftResponse.Length != 10 || !shiftResponse.StartsWith("IS00", StringComparison.Ordinal) ||
                shiftResponse[4] is not ('+' or '-') ||
                !int.TryParse(shiftResponse.AsSpan(5, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int magnitude) ||
                magnitude > 1200 || magnitude % 20 != 0)
            {
                throw new YaesuProtocolException($"Invalid {control} response '{shiftResponse}'.");
            }

            int shift = shiftResponse[4] == '-' ? -magnitude : magnitude;
            return new RadioControlValue(control, shift, DateTimeOffset.UtcNow);
        }

        if (control == RadioControlId.ClarifierOffsetHz)
        {
            string clarifierResponse = await _protocol.QueryAsync("CF001", "CF", cancellationToken).ConfigureAwait(false);
            if (clarifierResponse.Length != 11 || !clarifierResponse.StartsWith("CF001", StringComparison.Ordinal) ||
                clarifierResponse[5] is not ('+' or '-') ||
                !int.TryParse(clarifierResponse.AsSpan(6, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int magnitude))
            {
                throw new YaesuProtocolException($"Invalid {control} response '{clarifierResponse}'.");
            }

            int offset = clarifierResponse[5] == '-' ? -magnitude : magnitude;
            return new RadioControlValue(control, offset, DateTimeOffset.UtcNow);
        }

        string response = await _protocol.QueryAsync(command.Query, command.ResponsePrefix[..2], cancellationToken).ConfigureAwait(false);
        int valueOffset = command.ResponsePrefix.Length;
        if (response.Length != valueOffset + command.Digits + 1 ||
            !response.StartsWith(command.ResponsePrefix, StringComparison.Ordinal) ||
            !int.TryParse(response.AsSpan(valueOffset, command.Digits), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
            value * command.Scale + command.ValueOffset < command.Minimum ||
            value * command.Scale + command.ValueOffset > command.Maximum)
        {
            throw new YaesuProtocolException($"Invalid {control} response '{response}'.");
        }

        return new RadioControlValue(control, value * command.Scale + command.ValueOffset, DateTimeOffset.UtcNow);
    }

    public ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ControlCommand command = GetControlCommand(control);
        if (value < command.Minimum || value > command.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if ((value - command.Minimum) % command.Scale != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (control == RadioControlId.IfShiftHz)
        {
            return _protocol.SendAsync(
                $"IS00{(value < 0 ? '-' : '+')}{Math.Abs(value).ToString("D4", CultureInfo.InvariantCulture)}",
                cancellationToken);
        }

        if (control == RadioControlId.ClarifierOffsetHz)
        {
            return _protocol.SendAsync(
                $"CF001{(value < 0 ? '-' : '+')}{Math.Abs(value).ToString("D4", CultureInfo.InvariantCulture)}",
                cancellationToken);
        }

        int encoded = (value - command.ValueOffset) / command.Scale;
        return _protocol.SendAsync(
            $"{command.Query}{encoded.ToString($"D{command.Digits}", CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!MeterCommands.TryGetValue(meter, out MeterCommand? command))
        {
            throw new NotSupportedException($"Meter '{meter}' is not supported by the FTDX10 CAT profile.");
        }

        string response = await _protocol.QueryAsync(command.Query, command.ResponsePrefix[..2], cancellationToken).ConfigureAwait(false);
        if (response.Length != command.ResponseLength ||
            !response.StartsWith(command.ResponsePrefix, StringComparison.Ordinal) ||
            !int.TryParse(response.AsSpan(3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int raw) ||
            raw is < 0 or > 255)
        {
            throw new YaesuProtocolException($"Invalid {meter} meter response '{response}'.");
        }

        return new RadioMeterReading(meter, raw, raw / 255d, DateTimeOffset.UtcNow);
    }

    public async ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        SwitchCommand command = GetSwitchCommand(control);
        string response = await _protocol.QueryAsync(command.Query, command.ResponsePrefix[..2], cancellationToken).ConfigureAwait(false);
        int expectedLength = command.ResponsePrefix.Length + command.ValueDigits + 1;
        if (response.Length != expectedLength || !response.StartsWith(command.ResponsePrefix, StringComparison.Ordinal))
        {
            throw new YaesuProtocolException($"Invalid {control} response '{response}'.");
        }

        string encoded = response.Substring(command.ResponsePrefix.Length, command.ValueDigits);
        bool correctlyPadded = encoded[..^1].All(character => character == '0');
        bool enabled = encoded[^1] switch
        {
            var value when correctlyPadded && value == command.DisabledCode => false,
            var value when correctlyPadded && value == command.EnabledCode => true,
            _ => throw new YaesuProtocolException($"Invalid {control} response '{response}'.")
        };
        return new RadioSwitchValue(control, enabled, DateTimeOffset.UtcNow);
    }

    public ValueTask WriteSwitchAsync(
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        SwitchCommand command = GetSwitchCommand(control);
        char code = enabled ? command.EnabledCode : command.DisabledCode;
        return _protocol.SendAsync($"{command.SetPrefix}{new string('0', command.ValueDigits - 1)}{code}", cancellationToken);
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioChoiceId.VoxDelay)
            return await ReadVoxDelayAsync(cancellationToken).ConfigureAwait(false);
        if (control == RadioChoiceId.TuningStep)
            return await ReadTuningStepAsync(cancellationToken).ConfigureAwait(false);
        if (control == RadioChoiceId.FilterWidth)
        {
            RadioMode mode = await ReadActiveModeAsync(cancellationToken).ConfigureAwait(false);
            string widthResponse = await _protocol.QueryAsync("SH0", "SH", cancellationToken).ConfigureAwait(false);
            if (widthResponse.Length != 7 || !widthResponse.StartsWith("SH00", StringComparison.Ordinal) ||
                !int.TryParse(widthResponse.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int widthCode))
            {
                throw new YaesuProtocolException($"Invalid {control} response '{widthResponse}'.");
            }

            string value = DecodeFilterWidth(mode, widthCode);
            return new RadioChoiceValue(control, value, DateTimeOffset.UtcNow);
        }

        ChoiceCommand command = GetChoiceCommand(control);
        string response = await _protocol.QueryAsync(command.Query, command.ResponsePrefix[..2], cancellationToken).ConfigureAwait(false);
        if (response.Length != command.ResponsePrefix.Length + 2 ||
            !response.StartsWith(command.ResponsePrefix, StringComparison.Ordinal))
        {
            throw new YaesuProtocolException($"Invalid {control} response '{response}'.");
        }

        char code = response[^2];
        KeyValuePair<string, ChoiceCode> match = command.Options.FirstOrDefault(pair =>
            (pair.Value.ReadCode ?? pair.Value.Code) == code &&
            (!pair.Value.Writable || !command.Options.Any(other =>
                !other.Value.Writable && (other.Value.ReadCode ?? other.Value.Code) == code)));
        if (match.Key is null)
        {
            throw new YaesuProtocolException($"Invalid {control} response '{response}'.");
        }

        return new RadioChoiceValue(control, match.Key, DateTimeOffset.UtcNow);
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioChoiceId.VoxDelay)
            return WriteVoxDelayAsync(value, cancellationToken);
        if (control == RadioChoiceId.TuningStep)
            return WriteTuningStepAsync(value, cancellationToken);
        if (control == RadioChoiceId.FilterWidth)
        {
            return WriteFilterWidthAsync(value, cancellationToken);
        }

        ChoiceCommand command = GetChoiceCommand(control);
        if (!command.Options.TryGetValue(value, out ChoiceCode? option) || !option.Writable)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return _protocol.SendAsync($"{command.Query}{option.Code}", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_automaticInformationEnabled)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await _protocol.SendAsync("AI0", cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
                {
                    // Best-effort cleanup; transport disposal remains mandatory.
                }
            }

            try
            {
                // Closing the transport is what reliably unblocks SerialPort.BaseStream.ReadAsync
                // on Windows; cancellation alone is not sufficient on every driver/runtime pair.
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _protocol.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static long ParseFrequency(string response)
    {
        if (response.Length != 12 ||
            !long.TryParse(response.AsSpan(2, 9), NumberStyles.None, CultureInfo.InvariantCulture, out long frequency))
        {
            throw new YaesuProtocolException($"Invalid frequency response '{response}'.");
        }

        return frequency;
    }

    private static VfoId ParseVfo(string response) => response switch
    {
        "VS0;" => VfoId.A,
        "VS1;" => VfoId.B,
        _ => throw new YaesuProtocolException($"Invalid VFO selection response '{response}'.")
    };

    private static RadioMode ParseMode(string response)
    {
        if (response.Length != 5 || !Ftdx10CatProfile.Modes.TryGetValue(response[3], out RadioMode mode))
        {
            throw new YaesuProtocolException($"Invalid operating mode response '{response}'.");
        }

        return mode;
    }

    private static bool ParseBoolean(string response) => response switch
    {
        "ST0;" => false,
        "ST1;" or "ST2;" => true,
        _ => throw new YaesuProtocolException($"Invalid split response '{response}'.")
    };

    private static bool ParseTransmit(string response) => response switch
    {
        "TX0;" => false,
        "TX1;" or "TX2;" => true,
        _ => throw new YaesuProtocolException($"Invalid transmit response '{response}'.")
    };

    private static RadioDriverObservation ParseObservation(string frame)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        try
        {
            if (frame.StartsWith("FA", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.FrequencyChanged, observedAt, frame, VfoId.A, ParseFrequency(frame));
            if (frame.StartsWith("FB", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.FrequencyChanged, observedAt, frame, VfoId.B, ParseFrequency(frame));
            if (frame.StartsWith("VS", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.ActiveVfoChanged, observedAt, frame, ParseVfo(frame));
            if (frame.StartsWith("MD", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.ModeChanged, observedAt, frame, Mode: ParseMode(frame));
            if (frame.StartsWith("ST", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.SplitChanged, observedAt, frame, Flag: ParseBoolean(frame));
            if (frame.StartsWith("TX", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.TransmitChanged, observedAt, frame, Flag: ParseTransmit(frame));
            if (frame.StartsWith("IF", StringComparison.OrdinalIgnoreCase))
            {
                if (frame.Length != 28 || frame[^1] != ';' ||
                    !long.TryParse(frame.AsSpan(5, 9), NumberStyles.None, CultureInfo.InvariantCulture, out long frequency) ||
                    !Ftdx10CatProfile.Modes.TryGetValue(frame[21], out RadioMode mode))
                {
                    throw new YaesuProtocolException($"Invalid information response '{frame}'.");
                }

                return new(
                    RadioDriverObservationKind.StateInformation,
                    observedAt,
                    frame,
                    VfoId.A,
                    frequency,
                    mode);
            }
            if (frame.StartsWith("RM0", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.Ignored, observedAt, frame);
            if (IsSpectrumDisplayFrequencyFrame(frame))
                return new(RadioDriverObservationKind.Ignored, observedAt, frame);
        }
        catch (YaesuProtocolException)
        {
            // Preserve malformed or unsupported announcements as diagnostics.
        }

        return new(RadioDriverObservationKind.Unknown, observedAt, frame);
    }

    private static bool IsSpectrumDisplayFrequencyFrame(string frame) =>
        frame.Length == 15 &&
        frame.StartsWith("FD", StringComparison.OrdinalIgnoreCase) &&
        frame[^1] == ';' &&
        frame.AsSpan(2, 12).IndexOfAnyExceptInRange('0', '9') < 0;

    private static RadioCapabilities CreateCapabilities()
    {
        var readWrite = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return new RadioCapabilities(
            1,
            "Yaesu",
            "FTDX10",
            "rig2cast.drivers.yaesu.ftdx10",
            "0.1.0",
            new VfoCapability(new HashSet<VfoId> { VfoId.A, VfoId.B, VfoId.Memory }, readWrite, readWrite),
            new FrequencyCapability(
                readWrite,
                new HashSet<VfoId> { VfoId.A, VfoId.B },
                [new FrequencyRange(30_000, 75_000_000, true, false)],
                1),
            new ModeCapability(readWrite, Ftdx10CatProfile.Modes.Values.ToHashSet()),
            new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write, LeaseKinds.Transmit),
            CreateControlCapabilities(),
            CreateSwitchCapabilities(),
            CreateChoiceCapabilities(),
            CreateMeterCapabilities(),
            new Dictionary<string, object?>
            {
                ["yaesu.cat.identification"] = Ftdx10CatProfile.Identification,
                ["serial.supportedBaudRates"] = Ftdx10CatProfile.SupportedBaudRates,
                ["serial.dataBits"] = 8,
                ["serial.stopBits"] = 2,
                ["serial.parity"] = "None",
                ["serial.handshake"] = "RequestToSend",
                ["yaesu.autoInformation.supportedOnUsb"] = true,
                ["rig2cast.coverage"] = "core-vfo-mode-split-ptt-controls-meters-features"
            });
    }

    private static Dictionary<RadioControlId, NumericControlDescriptor> CreateControlCapabilities()
    {
        var feature = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return ControlCommands.ToDictionary(
            pair => pair.Key,
            pair => new NumericControlDescriptor(
                pair.Key,
                pair.Value.DisplayName,
                feature,
                pair.Value.Minimum,
                pair.Value.Maximum,
                pair.Value.Scale,
                pair.Value.Unit));
    }

    private static Dictionary<RadioMeterId, RadioMeterDescriptor> CreateMeterCapabilities() =>
        MeterCommands.ToDictionary(
            pair => pair.Key,
            pair => new RadioMeterDescriptor(pair.Key, pair.Value.DisplayName, 0, 255, "raw", false));

    private static Dictionary<RadioSwitchId, SwitchControlDescriptor> CreateSwitchCapabilities()
    {
        var feature = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return SwitchCommands.ToDictionary(
            pair => pair.Key,
            pair => new SwitchControlDescriptor(pair.Key, pair.Value.DisplayName, feature));
    }

    private static Dictionary<RadioChoiceId, ChoiceControlDescriptor> CreateChoiceCapabilities()
    {
        var feature = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        Dictionary<RadioChoiceId, ChoiceControlDescriptor> choices = ChoiceCommands.ToDictionary(
            pair => pair.Key,
            pair => new ChoiceControlDescriptor(
                pair.Key,
                pair.Value.DisplayName,
                feature,
                pair.Value.Options.ToDictionary(
                    option => option.Key,
                    option => new RadioChoiceOption(option.Key, option.Value.DisplayName, option.Value.Writable))));
        choices[RadioChoiceId.FilterWidth] = CreateFilterWidthCapability(feature);
        choices[RadioChoiceId.VoxDelay] = CreateVoxDelayCapability(feature);
        choices[RadioChoiceId.TuningStep] = CreateTuningStepCapability(feature);
        return choices;
    }

    private static ChoiceControlDescriptor CreateVoxDelayCapability(FeatureDescriptor feature)
    {
        var options = new Dictionary<string, RadioChoiceOption> { ["off"] = new("off", "Off") };
        for (int milliseconds = 100; milliseconds <= 3000; milliseconds += 100)
            options[$"{milliseconds}ms"] = new($"{milliseconds}ms", $"{milliseconds} ms");
        return new ChoiceControlDescriptor(RadioChoiceId.VoxDelay, "VOX delay", feature, options);
    }

    private static ChoiceControlDescriptor CreateTuningStepCapability(FeatureDescriptor feature)
    {
        HashSet<RadioMode> ssbCw =
            [RadioMode.Lsb, RadioMode.Usb, RadioMode.Cw, RadioMode.CwReverse, RadioMode.Rtty,
             RadioMode.RttyReverse, RadioMode.DataLsb, RadioMode.DataUsb, RadioMode.Psk];
        HashSet<RadioMode> amFm =
            [RadioMode.Am, RadioMode.AmNarrow, RadioMode.Fm, RadioMode.FmNarrow,
             RadioMode.DataFm, RadioMode.DataFmNarrow];
        return new ChoiceControlDescriptor(RadioChoiceId.TuningStep, "VFO tuning step", feature,
            new Dictionary<string, RadioChoiceOption>
            {
                ["10hz"] = new("10hz", "10 Hz", true, ssbCw),
                ["100hz"] = new("100hz", "100 Hz", true, ssbCw.Concat(amFm).ToHashSet()),
                ["1khz"] = new("1khz", "1 kHz", true, amFm)
            });
    }

    private static ChoiceControlDescriptor CreateFilterWidthCapability(FeatureDescriptor feature)
    {
        HashSet<RadioMode> ssbModes = [RadioMode.Lsb, RadioMode.Usb];
        HashSet<RadioMode> narrowModes =
            [RadioMode.Cw, RadioMode.CwReverse, RadioMode.Rtty, RadioMode.RttyReverse, RadioMode.Psk, RadioMode.DataLsb, RadioMode.DataUsb];
        var options = new Dictionary<string, RadioChoiceOption>
        {
            ["default"] = new("default", "Default", true, ssbModes.Concat(narrowModes).ToHashSet())
        };
        foreach (int width in SsbFilterWidths.Where(value => value > 0))
        {
            options[$"{width}hz"] = new($"{width}hz", $"{width} Hz", true, new HashSet<RadioMode>(ssbModes));
        }
        foreach (int width in NarrowFilterWidths.Where(value => value > 0))
        {
            string key = $"{width}hz";
            if (options.TryGetValue(key, out RadioChoiceOption? existing))
            {
                options[key] = existing with { ApplicableModes = existing.ApplicableModes!.Concat(narrowModes).ToHashSet() };
            }
            else
            {
                options[key] = new(key, $"{width} Hz", true, new HashSet<RadioMode>(narrowModes));
            }
        }
        return new ChoiceControlDescriptor(RadioChoiceId.FilterWidth, "Filter width", feature, options);
    }

    private async ValueTask<RadioChoiceValue> ReadVoxDelayAsync(CancellationToken cancellationToken)
    {
        string response = await _protocol.QueryAsync("VD", "VD", cancellationToken).ConfigureAwait(false);
        if (response.Length != 5 || !int.TryParse(response.AsSpan(2, 2), NumberStyles.None,
                CultureInfo.InvariantCulture, out int code))
            throw new YaesuProtocolException($"Invalid VOX delay response '{response}'.");
        int milliseconds = code switch
        {
            0 => 0, 2 => 100, 4 => 200,
            >= 6 and <= 33 => (code - 6) * 100 + 300,
            _ => throw new YaesuProtocolException($"Invalid VOX delay code '{code:D2}'.")
        };
        return new RadioChoiceValue(RadioChoiceId.VoxDelay,
            milliseconds == 0 ? "off" : $"{milliseconds}ms", DateTimeOffset.UtcNow);
    }

    private ValueTask WriteVoxDelayAsync(string value, CancellationToken cancellationToken)
    {
        int milliseconds;
        if (StringComparer.OrdinalIgnoreCase.Equals(value, "off")) milliseconds = 0;
        else if (!value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ||
                 !int.TryParse(value.AsSpan(0, value.Length - 2), NumberStyles.None,
                     CultureInfo.InvariantCulture, out milliseconds) ||
                 milliseconds is < 100 or > 3000 || milliseconds % 100 != 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        int code = milliseconds switch { 0 => 0, 100 => 2, 200 => 4, _ => (milliseconds - 300) / 100 + 6 };
        return _protocol.SendAsync($"VD{code:D2}", cancellationToken);
    }

    private async ValueTask<RadioChoiceValue> ReadTuningStepAsync(CancellationToken cancellationToken)
    {
        RadioMode mode = await ReadActiveModeAsync(cancellationToken).ConfigureAwait(false);
        string response = await _protocol.QueryAsync("FS", "FS", cancellationToken).ConfigureAwait(false);
        if (response.Length != 4 || response[2] is not ('0' or '1'))
            throw new YaesuProtocolException($"Invalid fast-step response '{response}'.");
        (string normal, string fast) = GetTuningSteps(mode);
        return new RadioChoiceValue(RadioChoiceId.TuningStep,
            response[2] == '1' ? fast : normal, DateTimeOffset.UtcNow);
    }

    private async ValueTask WriteTuningStepAsync(string value, CancellationToken cancellationToken)
    {
        RadioMode mode = await ReadActiveModeAsync(cancellationToken).ConfigureAwait(false);
        (string normal, string fast) = GetTuningSteps(mode);
        bool enabled = StringComparer.OrdinalIgnoreCase.Equals(value, fast)
            ? true
            : StringComparer.OrdinalIgnoreCase.Equals(value, normal)
                ? false
                : throw new ArgumentOutOfRangeException(nameof(value), $"Tuning step '{value}' is not valid in {mode} mode.");
        await _protocol.SendAsync(enabled ? "FS1" : "FS0", cancellationToken).ConfigureAwait(false);
    }

    private static (string Normal, string Fast) GetTuningSteps(RadioMode mode) => mode switch
    {
        RadioMode.Lsb or RadioMode.Usb or RadioMode.Cw or RadioMode.CwReverse or RadioMode.Rtty or
            RadioMode.RttyReverse or RadioMode.DataLsb or RadioMode.DataUsb or RadioMode.Psk => ("10hz", "100hz"),
        RadioMode.Am or RadioMode.AmNarrow or RadioMode.Fm or RadioMode.FmNarrow or
            RadioMode.DataFm or RadioMode.DataFmNarrow => ("100hz", "1khz"),
        _ => throw new NotSupportedException($"Tuning step is not available in {mode} mode.")
    };

    private async ValueTask<RadioMode> ReadActiveModeAsync(CancellationToken cancellationToken)
    {
        VfoId active = ParseVfo(await _protocol.QueryAsync("VS", "VS", cancellationToken).ConfigureAwait(false));
        return ParseMode(await _protocol.QueryAsync(active == VfoId.A ? "MD0" : "MD1", "MD", cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask WriteFilterWidthAsync(string value, CancellationToken cancellationToken)
    {
        RadioMode mode = await ReadActiveModeAsync(cancellationToken).ConfigureAwait(false);
        int code = EncodeFilterWidth(mode, value);
        await _protocol.SendAsync($"SH00{code:D2}", cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeFilterWidth(RadioMode mode, int code)
    {
        int[] widths = GetFilterWidths(mode);
        if (code < 0 || code >= widths.Length)
        {
            throw new YaesuProtocolException($"Filter width code '{code:D2}' is invalid for {mode} mode.");
        }
        return code == 0 ? "default" : $"{widths[code]}hz";
    }

    private static int EncodeFilterWidth(RadioMode mode, string value)
    {
        if (value == "default") return 0;
        if (!value.EndsWith("hz", StringComparison.Ordinal) ||
            !int.TryParse(value.AsSpan(0, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int width))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        int code = Array.IndexOf(GetFilterWidths(mode), width);
        return code > 0 ? code : throw new ArgumentOutOfRangeException(nameof(value), $"Width {value} is not valid in {mode} mode.");
    }

    private static int[] GetFilterWidths(RadioMode mode) => mode switch
    {
        RadioMode.Lsb or RadioMode.Usb => SsbFilterWidths,
        RadioMode.Cw or RadioMode.CwReverse or RadioMode.Rtty or RadioMode.RttyReverse or
            RadioMode.Psk or RadioMode.DataLsb or RadioMode.DataUsb => NarrowFilterWidths,
        _ => throw new NotSupportedException($"FTDX10 CAT filter width is not available in {mode} mode.")
    };

    private static readonly int[] SsbFilterWidths =
        [0, 300, 400, 600, 850, 1100, 1200, 1500, 1650, 1800, 1950, 2100, 2250, 2400, 2450, 2500, 2600, 2700, 2800, 2900, 3000, 3200, 3500, 4000];
    private static readonly int[] NarrowFilterWidths =
        [0, 50, 100, 150, 200, 250, 300, 350, 400, 450, 500, 600, 800, 1200, 1400, 1700, 2000, 2400, 3000, 3200, 3500, 4000];

    private static ControlCommand GetControlCommand(RadioControlId control) =>
        ControlCommands.TryGetValue(control, out ControlCommand? command)
            ? command
            : throw new NotSupportedException($"Control '{control}' is not supported by the FTDX10 CAT profile.");

    private static SwitchCommand GetSwitchCommand(RadioSwitchId control) =>
        SwitchCommands.TryGetValue(control, out SwitchCommand? command)
            ? command
            : throw new NotSupportedException($"Switch '{control}' is not supported by the FTDX10 CAT profile.");

    private static ChoiceCommand GetChoiceCommand(RadioChoiceId control) =>
        ChoiceCommands.TryGetValue(control, out ChoiceCommand? command)
            ? command
            : throw new NotSupportedException($"Choice '{control}' is not supported by the FTDX10 CAT profile.");

    private void EnsureActive() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ControlCommand(
        string DisplayName,
        string Query,
        string ResponsePrefix,
        int Digits,
        int Minimum,
        int Maximum,
        string Unit,
        int Scale = 1,
        int ValueOffset = 0);

    private sealed record MeterCommand(
        string DisplayName,
        string Query,
        string ResponsePrefix,
        int ResponseLength);

    private sealed record SwitchCommand(
        string DisplayName,
        string Query,
        string ResponsePrefix,
        string SetPrefix,
        char DisabledCode,
        char EnabledCode,
        int ValueDigits = 1);

    private sealed record ChoiceCommand(
        string DisplayName,
        string Query,
        string ResponsePrefix,
        IReadOnlyDictionary<string, ChoiceCode> Options);

    private sealed record ChoiceCode(char Code, string DisplayName, bool Writable = true, char? ReadCode = null);
}
