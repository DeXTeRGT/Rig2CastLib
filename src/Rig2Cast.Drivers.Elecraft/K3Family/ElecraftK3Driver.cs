using System.Globalization;
using System.Diagnostics;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Elecraft.Protocol;

namespace Rig2Cast.Drivers.Elecraft.K3Family;

public sealed class ElecraftK3Driver : IRadioDriver, IRadioObservationSource,
    IRadioControlDriver, IRadioSwitchDriver, IRadioChoiceDriver, IRadioPassbandDriver,
    IRadioMeterDriver, IRadioTargetedControlDriver, IRadioTargetedChoiceDriver,
    IRadioTargetedPassbandDriver, IRadioTargetedMeterDriver
{
    private readonly IRadioTransport _transport;
    private readonly ElecraftAsciiProtocol _protocol;
    private readonly ElecraftK3Profile _profile;
    private readonly string _optionResponse;
    private readonly int _automaticInformationMode;
    private int _disposed;

    private ElecraftK3Driver(
        IRadioTransport transport,
        ElecraftAsciiProtocol protocol,
        ElecraftK3Profile profile,
        string optionResponse,
        int automaticInformationMode)
    {
        _transport = transport;
        _protocol = protocol;
        _profile = profile;
        _optionResponse = optionResponse;
        _automaticInformationMode = automaticInformationMode;
        Capabilities = CreateCapabilities(profile, optionResponse);
    }

    public RadioCapabilities Capabilities { get; }

    public static async ValueTask<ElecraftK3Driver> OpenAsync(
        IRadioTransport transport,
        ElecraftK3Profile profile,
        bool enableAutomaticInformation = false,
        int automaticInformationMode = 1,
        TimeSpan? responseTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(profile);
        var protocol = default(ElecraftAsciiProtocol);
        try
        {
            if (!transport.IsConnected)
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            protocol = new ElecraftAsciiProtocol(transport, responseTimeout);
            string options = await protocol.QueryAsync("OM", "OM", cancellationToken).ConfigureAwait(false);
            if (!profile.MatchesOptionResponse(options))
                throw new ElecraftProtocolException(
                    $"Connected radio option response '{options}' does not match requested model {profile.Model}.");
            if (automaticInformationMode is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(automaticInformationMode));
            int selectedAutoInformationMode = enableAutomaticInformation ? automaticInformationMode : 0;
            if (selectedAutoInformationMode > 0)
            {
                // FW has legacy K2-dependent layouts. K31 makes AI2/AI3 FW
                // announcements unambiguously report bandwidth as four 10-Hz digits.
                await protocol.SendAsync("K31", cancellationToken).ConfigureAwait(false);
                await protocol.SendAsync($"AI{selectedAutoInformationMode}", cancellationToken).ConfigureAwait(false);
            }
            return new ElecraftK3Driver(transport, protocol, profile, options, selectedAutoInformationMode);
        }
        catch
        {
            if (protocol is not null)
                await protocol.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        long frequencyA = ParseFrequency(await _protocol.QueryAsync("FA", "FA", cancellationToken).ConfigureAwait(false));
        long frequencyB = ParseFrequency(await _protocol.QueryAsync("FB", "FB", cancellationToken).ConfigureAwait(false));
        ElecraftInformation information = ParseInformation(
            await _protocol.QueryAsync("IF", "IF", cancellationToken).ConfigureAwait(false));
        VfoId transmitVfo = ParseVfo(await _protocol.QueryAsync("FT", "FT", cancellationToken).ConfigureAwait(false));
        bool transmitting = ParseTransmit(await _protocol.QueryAsync("TQ", "TQ", cancellationToken).ConfigureAwait(false));
        return new RadioState(
            1,
            ConnectionStatus.Connected,
            new Dictionary<VfoId, long> { [VfoId.A] = frequencyA, [VfoId.B] = frequencyB },
            information.ActiveVfo,
            information.Mode,
            information.IsSplit,
            transmitting,
            DateTimeOffset.UtcNow)
        {
            TransmitVfo = transmitVfo
        };
    }

    public ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentOutOfRangeException.ThrowIfLessThan(frequencyHz, 100_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frequencyHz, 54_000_000);
        string prefix = target switch
        {
            VfoId.A => "FA",
            VfoId.B => "FB",
            _ => throw new NotSupportedException($"Elecraft frequency targeting does not support VFO '{target}'.")
        };
        return _protocol.SendAsync($"{prefix}{frequencyHz:00000000000}", cancellationToken);
    }

    public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException(
            "K3-family FR does not provide conventional active receive-VFO selection; it cancels split."));

    public async ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ElecraftInformation information = ParseInformation(
            await _protocol.QueryAsync("IF", "IF", cancellationToken).ConfigureAwait(false));
        string prefix = information.ActiveVfo == VfoId.B ? "MD$" : "MD";
        await _protocol.SendAsync($"{prefix}{_profile.EncodeMode(mode)}", cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
        enabled ? _protocol.SendAsync("FT1", cancellationToken) : _protocol.SendAsync("FR0", cancellationToken);

    public ValueTask SetSplitAsync(
        bool enabled,
        VfoId transmitVfo,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!enabled)
            return _protocol.SendAsync("FR0", cancellationToken);
        return transmitVfo switch
        {
            VfoId.A => _protocol.SendAsync("FT0", cancellationToken),
            VfoId.B => _protocol.SendAsync("FT1", cancellationToken),
            _ => throw new NotSupportedException($"Elecraft split transmit does not support VFO '{transmitVfo}'.")
        };
    }

    public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return _protocol.SendAsync(enabled ? "TX" : "RX", cancellationToken);
    }

    public async IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (string frame in _protocol.WatchUnsolicitedFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            int dropped = _protocol.ConsumeDroppedUnsolicitedFrameCount();
            if (dropped > 0)
                yield return new(RadioDriverObservationKind.DeliveryGap, DateTimeOffset.UtcNow, string.Empty,
                    DroppedFrames: dropped);
            yield return ParseObservation(frame);
        }
    }

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        string command = control switch
        {
            RadioControlId.AfGain => "AG",
            RadioControlId.RfGain => "RG",
            RadioControlId.TransmitPower => "PC",
            RadioControlId.ClarifierOffsetHz => "RO",
            RadioControlId.KeyerSpeedWpm => "KS",
            _ => throw new NotSupportedException($"Control '{control}' is not supported by the Elecraft K3-family driver.")
        };
        string response = await _protocol.QueryAsync(command, command, cancellationToken).ConfigureAwait(false);
        int value = control == RadioControlId.ClarifierOffsetHz
            ? ParseSignedControl(response, command, 4, 9_999)
            : ParseUnsignedControl(response, command, 3, GetControlMaximum(control));
        return new RadioControlValue(control, value, DateTimeOffset.UtcNow);
    }

    public ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (control == RadioControlId.ClarifierOffsetHz)
        {
            if (value is < -9_999 or > 9_999)
                throw new ArgumentOutOfRangeException(nameof(value));
            return _protocol.SendAsync(
                $"RO{(value < 0 ? '-' : '+')}{Math.Abs(value).ToString("D4", CultureInfo.InvariantCulture)}",
                cancellationToken);
        }

        string command = control switch
        {
            RadioControlId.AfGain => "AG",
            RadioControlId.RfGain => "RG",
            RadioControlId.TransmitPower => "PC",
            RadioControlId.KeyerSpeedWpm => "KS",
            _ => throw new NotSupportedException($"Control '{control}' is not supported by the Elecraft K3-family driver.")
        };
        int maximum = GetControlMaximum(control);
        int minimum = control == RadioControlId.KeyerSpeedWpm ? 8 : 0;
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
        return _protocol.SendAsync($"{command}{value.ToString("D3", CultureInfo.InvariantCulture)}", cancellationToken);
    }

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        if (target == VfoId.A)
        {
            RadioControlValue value = await ReadControlAsync(control, cancellationToken).ConfigureAwait(false);
            return value with { Target = target };
        }
        string command = control switch
        {
            RadioControlId.AfGain => "AG$",
            RadioControlId.RfGain => "RG$",
            _ => throw new NotSupportedException($"Control '{control}' cannot target the Elecraft sub receiver.")
        };
        string response = await _protocol.QueryAsync(command, command, cancellationToken).ConfigureAwait(false);
        return new RadioControlValue(
            control,
            ParseUnsignedControl(response, command, 3, GetControlMaximum(control)),
            DateTimeOffset.UtcNow,
            target);
    }

    public ValueTask WriteControlAsync(
        RadioControlId control, VfoId target, int value, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        if (target == VfoId.A)
            return WriteControlAsync(control, value, cancellationToken);
        string command = control switch
        {
            RadioControlId.AfGain => "AG$",
            RadioControlId.RfGain => "RG$",
            _ => throw new NotSupportedException($"Control '{control}' cannot target the Elecraft sub receiver.")
        };
        int maximum = GetControlMaximum(control);
        if (value is < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
        return _protocol.SendAsync($"{command}{value.ToString("D3", CultureInfo.InvariantCulture)}", cancellationToken);
    }

    public async ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        string command = control switch
        {
            RadioSwitchId.ReceiveClarifier => "RT",
            RadioSwitchId.TransmitClarifier => "XT",
            _ => throw new NotSupportedException($"Switch '{control}' is not supported by the Elecraft K3-family driver.")
        };
        string response = await _protocol.QueryAsync(command, command, cancellationToken).ConfigureAwait(false);
        bool enabled = response switch
        {
            var value when value == $"{command}0;" => false,
            var value when value == $"{command}1;" => true,
            _ => throw new ElecraftProtocolException($"Invalid {control} response '{response}'.")
        };
        return new RadioSwitchValue(control, enabled, DateTimeOffset.UtcNow);
    }

    public ValueTask WriteSwitchAsync(
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        string command = control switch
        {
            RadioSwitchId.ReceiveClarifier => "RT",
            RadioSwitchId.TransmitClarifier => "XT",
            _ => throw new NotSupportedException($"Switch '{control}' is not supported by the Elecraft K3-family driver.")
        };
        return _protocol.SendAsync($"{command}{(enabled ? '1' : '0')}", cancellationToken);
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        string command = control switch
        {
            RadioChoiceId.Agc => "GT",
            RadioChoiceId.Attenuator => "RA",
            RadioChoiceId.Preamp => "PA",
            _ => throw new NotSupportedException($"Choice '{control}' is not supported by the Elecraft K3-family driver.")
        };
        string response = await _protocol.QueryAsync(command, command, cancellationToken).ConfigureAwait(false);
        string value = control switch
        {
            RadioChoiceId.Agc => response switch
            {
                "GT002;" => "fast",
                "GT004;" => "slow",
                _ => throw new ElecraftProtocolException($"Invalid AGC response '{response}'.")
            },
            RadioChoiceId.Attenuator => DecodeAttenuator(response),
            RadioChoiceId.Preamp => response switch
            {
                "PA0;" => "off",
                "PA1;" => "preamp1",
                "PA2;" => "preamp2",
                _ => throw new ElecraftProtocolException($"Invalid preamp response '{response}'.")
            },
            _ => throw new UnreachableException()
        };
        return new RadioChoiceValue(control, value, DateTimeOffset.UtcNow);
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        string command = control switch
        {
            RadioChoiceId.Agc => value.ToLowerInvariant() switch
            {
                "fast" => "GT002",
                "slow" => "GT004",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            },
            RadioChoiceId.Attenuator => EncodeAttenuator(value),
            RadioChoiceId.Preamp => value.ToLowerInvariant() switch
            {
                "off" => "PA0",
                "preamp1" => "PA1",
                "preamp2" when SupportsPreamp2 => "PA2",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            },
            _ => throw new NotSupportedException($"Choice '{control}' is not supported by the Elecraft K3-family driver.")
        };
        return _protocol.SendAsync(command, cancellationToken);
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        if (target == VfoId.A)
        {
            RadioChoiceValue primaryValue = await ReadChoiceAsync(control, cancellationToken).ConfigureAwait(false);
            return primaryValue with { Target = target };
        }
        string command = control switch
        {
            RadioChoiceId.Attenuator => "RA$",
            RadioChoiceId.Preamp => "PA$",
            _ => throw new NotSupportedException($"Choice '{control}' cannot target the Elecraft sub receiver.")
        };
        string response = await _protocol.QueryAsync(command, command, cancellationToken).ConfigureAwait(false);
        string value = control switch
        {
            RadioChoiceId.Attenuator => DecodeSubAttenuator(response),
            RadioChoiceId.Preamp => response switch
            {
                "PA$0;" => "off", "PA$1;" => "preamp1", "PA$2;" => "preamp2",
                _ => throw new ElecraftProtocolException($"Invalid sub-receiver preamp response '{response}'.")
            },
            _ => throw new UnreachableException()
        };
        return new RadioChoiceValue(control, value, DateTimeOffset.UtcNow, target);
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control, VfoId target, string value, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        if (target == VfoId.A)
            return WriteChoiceAsync(control, value, cancellationToken);
        string command = control switch
        {
            RadioChoiceId.Attenuator => value.ToLowerInvariant() switch
            {
                "off" => "RA$00", "10db" => "RA$10",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            },
            RadioChoiceId.Preamp => value.ToLowerInvariant() switch
            {
                "off" => "PA$0", "preamp1" => "PA$1",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            },
            _ => throw new NotSupportedException($"Choice '{control}' cannot target the Elecraft sub receiver.")
        };
        return _protocol.SendAsync(command, cancellationToken);
    }

    public async ValueTask<RadioPassbandValue> ReadPassbandAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        string response = await _protocol.QueryAsync("BW", "BW", cancellationToken).ConfigureAwait(false);
        int units = ParseUnsignedControl(response, "BW", 4, 9_999);
        return new RadioPassbandValue(units * 10, DateTimeOffset.UtcNow);
    }

    public ValueTask SetPassbandAsync(int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (widthHz is < 0 or > 99_990 || widthHz % 10 != 0)
            throw new ArgumentOutOfRangeException(nameof(widthHz));
        return _protocol.SendAsync($"BW{(widthHz / 10).ToString("D4", CultureInfo.InvariantCulture)}", cancellationToken);
    }

    public async ValueTask<RadioPassbandValue> ReadPassbandAsync(
        VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        string command = target == VfoId.B ? "BW$" : "BW";
        string response = await _protocol.QueryAsync(command, command, cancellationToken).ConfigureAwait(false);
        return new RadioPassbandValue(
            ParseUnsignedControl(response, command, 4, 9_999) * 10,
            DateTimeOffset.UtcNow,
            target);
    }

    public ValueTask SetPassbandAsync(
        VfoId target, int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        if (widthHz is < 0 or > 99_990 || widthHz % 10 != 0)
            throw new ArgumentOutOfRangeException(nameof(widthHz));
        string command = target == VfoId.B ? "BW$" : "BW";
        return _protocol.SendAsync($"{command}{(widthHz / 10).ToString("D4", CultureInfo.InvariantCulture)}", cancellationToken);
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        int raw;
        int minimum;
        int maximum;
        switch (meter)
        {
            case RadioMeterId.SignalStrength when IsK3Desktop:
                raw = ParseUnsignedControl(
                    await _protocol.QueryAsync("SMH", "SMH", cancellationToken).ConfigureAwait(false),
                    "SMH", 3, 140);
                minimum = 0;
                maximum = 140;
                break;
            case RadioMeterId.SignalStrength:
                raw = ParseUnsignedControl(
                    await _protocol.QueryAsync("SM", "SM", cancellationToken).ConfigureAwait(false),
                    "SM", 4, 15);
                minimum = 0;
                maximum = 15;
                break;
            case RadioMeterId.Swr:
                raw = ParseUnsignedControl(
                    await _protocol.QueryAsync("SW", "SW", cancellationToken).ConfigureAwait(false),
                    "SW", 3, 999);
                if (raw < 10)
                    throw new ElecraftProtocolException($"Invalid SWR response value '{raw:D3}'.");
                minimum = 10;
                maximum = 999;
                break;
            default:
                throw new NotSupportedException($"Meter '{meter}' is not supported by the {_profile.Model} profile.");
        }

        return new RadioMeterReading(
            meter,
            raw,
            (raw - minimum) / (double)(maximum - minimum),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureTargetSupported(target);
        if (target == VfoId.A)
        {
            RadioMeterReading reading = await ReadMeterAsync(meter, cancellationToken).ConfigureAwait(false);
            return reading with { Target = target };
        }
        if (meter != RadioMeterId.SignalStrength)
            throw new NotSupportedException($"Meter '{meter}' cannot target the Elecraft sub receiver.");
        int raw = ParseUnsignedControl(
            await _protocol.QueryAsync("SM$", "SM$", cancellationToken).ConfigureAwait(false),
            "SM$", 4, 15);
        return new RadioMeterReading(meter, raw, raw / 15d, DateTimeOffset.UtcNow, target);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_automaticInformationMode > 0)
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
            // Closing the port reliably releases a pending serial read on Windows.
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _protocol.DisposeAsync().ConfigureAwait(false);
        }
    }

    private RadioDriverObservation ParseObservation(string frame)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        try
        {
            if (frame.StartsWith("FA", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.FrequencyChanged, observedAt, frame, VfoId.A, ParseFrequency(frame));
            if (frame.StartsWith("FB", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.FrequencyChanged, observedAt, frame, VfoId.B, ParseFrequency(frame));
            if (frame.StartsWith("MD", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.ModeChanged, observedAt, frame, Mode: ParseMode(frame));
            if (frame.StartsWith("FT", StringComparison.OrdinalIgnoreCase))
            {
                VfoId transmitVfo = ParseVfo(frame);
                return new(
                    RadioDriverObservationKind.SplitChanged,
                    observedAt,
                    frame,
                    Flag: transmitVfo == VfoId.B,
                    TransmitVfo: transmitVfo);
            }
            if (frame.StartsWith("TQ", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.TransmitChanged, observedAt, frame, Flag: ParseTransmit(frame));
            if (frame.StartsWith("IF", StringComparison.OrdinalIgnoreCase))
            {
                ElecraftInformation information = ParseInformation(frame);
                return new(
                    RadioDriverObservationKind.StateInformation,
                    observedAt,
                    frame,
                    VfoId.A,
                    information.FrequencyHz,
                    information.Mode,
                    TransmitVfo: information.IsSplit ? VfoId.B : VfoId.A,
                    ActiveVfo: information.ActiveVfo,
                    IsSplit: information.IsSplit,
                    IsTransmitting: information.IsTransmitting);
            }
            if (frame.StartsWith("AG$", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.AfGain,
                    ParseUnsignedControl(frame, "AG$", 3, 255), observedAt, VfoId.B);
            if (frame.StartsWith("AG", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.AfGain, ParseUnsignedControl(frame, "AG", 3, 255), observedAt);
            if (frame.StartsWith("RG$", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.RfGain,
                    ParseUnsignedControl(frame, "RG$", 3, 250), observedAt, VfoId.B);
            if (frame.StartsWith("RG", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.RfGain, ParseUnsignedControl(frame, "RG", 3, 250), observedAt);
            if (frame.StartsWith("KS", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.KeyerSpeedWpm,
                    ParseUnsignedControl(frame, "KS", 3, 50), observedAt);
            if (frame.StartsWith("PC", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.TransmitPower,
                    ParseUnsignedControl(frame, "PC", 3, GetPowerMaximum(_profile, _optionResponse)), observedAt);
            if (frame.StartsWith("RO", StringComparison.OrdinalIgnoreCase))
                return NumericObservation(frame, RadioControlId.ClarifierOffsetHz,
                    ParseSignedControl(frame, "RO", 4, 9_999), observedAt);
            if (frame.StartsWith("RT", StringComparison.OrdinalIgnoreCase))
                return SwitchObservation(frame, RadioSwitchId.ReceiveClarifier, ParseBinary(frame, "RT"), observedAt);
            if (frame.StartsWith("XT", StringComparison.OrdinalIgnoreCase))
                return SwitchObservation(frame, RadioSwitchId.TransmitClarifier, ParseBinary(frame, "XT"), observedAt);
            if (frame.StartsWith("GT", StringComparison.OrdinalIgnoreCase))
            {
                string value = frame switch
                {
                    "GT002;" => "fast", "GT004;" => "slow",
                    _ => throw new ElecraftProtocolException($"Invalid AGC response '{frame}'.")
                };
                return ChoiceObservation(frame, RadioChoiceId.Agc, value, observedAt);
            }
            if (frame.StartsWith("RA$", StringComparison.OrdinalIgnoreCase))
                return ChoiceObservation(frame, RadioChoiceId.Attenuator, DecodeSubAttenuator(frame), observedAt, VfoId.B);
            if (frame.StartsWith("RA", StringComparison.OrdinalIgnoreCase))
                return ChoiceObservation(frame, RadioChoiceId.Attenuator, DecodeAttenuator(frame), observedAt);
            if (frame.StartsWith("PA$", StringComparison.OrdinalIgnoreCase))
            {
                string value = frame switch
                {
                    "PA$0;" => "off", "PA$1;" => "preamp1", "PA$2;" => "preamp2",
                    _ => throw new ElecraftProtocolException($"Invalid sub-receiver preamp response '{frame}'.")
                };
                return ChoiceObservation(frame, RadioChoiceId.Preamp, value, observedAt, VfoId.B);
            }
            if (frame.StartsWith("PA", StringComparison.OrdinalIgnoreCase))
            {
                string value = frame switch
                {
                    "PA0;" => "off", "PA1;" => "preamp1", "PA2;" => "preamp2",
                    _ => throw new ElecraftProtocolException($"Invalid preamp response '{frame}'.")
                };
                return ChoiceObservation(frame, RadioChoiceId.Preamp, value, observedAt);
            }
            if (frame.StartsWith("BW$", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.ControlChanged, observedAt, frame,
                    Passband: new(ParseUnsignedControl(frame, "BW$", 4, 9_999) * 10, observedAt, VfoId.B));
            if (frame.StartsWith("BW", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.ControlChanged, observedAt, frame,
                    Passband: new(ParseUnsignedControl(frame, "BW", 4, 9_999) * 10, observedAt));
            if (frame.StartsWith("FW", StringComparison.OrdinalIgnoreCase))
                return new(RadioDriverObservationKind.ControlChanged, observedAt, frame,
                    Passband: new(ParseFilterWidthAnnouncement(frame) * 10, observedAt));
            if (KnownControlPrefixes.Any(prefix => frame.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return new(RadioDriverObservationKind.Ignored, observedAt, frame);
        }
        catch (ElecraftProtocolException)
        {
            // Malformed announcements remain visible as diagnostics.
        }
        return new(RadioDriverObservationKind.Unknown, observedAt, frame);
    }

    private static RadioDriverObservation NumericObservation(
        string frame, RadioControlId id, int value, DateTimeOffset observedAt, VfoId? target = null) =>
        new(RadioDriverObservationKind.ControlChanged, observedAt, frame,
            NumericControl: new(id, value, observedAt, target));

    private static RadioDriverObservation SwitchObservation(
        string frame, RadioSwitchId id, bool value, DateTimeOffset observedAt) =>
        new(RadioDriverObservationKind.ControlChanged, observedAt, frame,
            SwitchControl: new(id, value, observedAt));

    private static RadioDriverObservation ChoiceObservation(
        string frame, RadioChoiceId id, string value, DateTimeOffset observedAt, VfoId? target = null) =>
        new(RadioDriverObservationKind.ControlChanged, observedAt, frame,
            ChoiceControl: new(id, value, observedAt, target));

    private static bool ParseBinary(string response, string prefix) => response switch
    {
        var value when value == $"{prefix}0;" => false,
        var value when value == $"{prefix}1;" => true,
        _ => throw new ElecraftProtocolException($"Invalid {prefix} response '{response}'.")
    };

    private static int ParseFilterWidthAnnouncement(string response)
    {
        if (response.Length != 7 || response[^1] != ';' ||
            !int.TryParse(response.AsSpan(2, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int units))
            throw new ElecraftProtocolException($"Invalid FW response '{response}'.");
        return units;
    }

    private static long ParseFrequency(string response)
    {
        if (response.Length != 14 || response[^1] != ';' ||
            !long.TryParse(response.AsSpan(2, 11), NumberStyles.None, CultureInfo.InvariantCulture, out long frequency))
            throw new ElecraftProtocolException($"Invalid frequency response '{response}'.");
        return frequency;
    }

    private static RadioMode ParseMode(string response)
    {
        int codeIndex = response.StartsWith("MD$", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        if (response.Length != codeIndex + 2 || response[^1] != ';' ||
            !ElecraftK3Profile.Modes.TryGetValue(response[codeIndex], out RadioMode mode))
            throw new ElecraftProtocolException($"Invalid mode response '{response}'.");
        return mode;
    }

    private static VfoId ParseVfo(string response) => response switch
    {
        "FT0;" => VfoId.A,
        "FT1;" => VfoId.B,
        _ => throw new ElecraftProtocolException($"Invalid transmit-VFO response '{response}'.")
    };

    private static bool ParseTransmit(string response) => response switch
    {
        "TQ0;" => false,
        "TQ1;" => true,
        _ => throw new ElecraftProtocolException($"Invalid transmit-state response '{response}'.")
    };

    private static ElecraftInformation ParseInformation(string response)
    {
        if (response.Length < 38 || response[^1] != ';' ||
            !long.TryParse(response.AsSpan(2, 11), NumberStyles.None, CultureInfo.InvariantCulture, out long frequency) ||
            !ElecraftK3Profile.Modes.TryGetValue(response[29], out RadioMode mode) ||
            response[28] is not ('0' or '1') || response[30] is not ('0' or '1') || response[32] is not ('0' or '1'))
            throw new ElecraftProtocolException($"Invalid information response '{response}'.");
        return new(
            frequency,
            mode,
            response[30] == '1' ? VfoId.B : VfoId.A,
            response[32] == '1',
            response[28] == '1');
    }

    private static RadioCapabilities CreateCapabilities(ElecraftK3Profile profile, string optionResponse)
    {
        var readWrite = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        var unsupported = new FeatureDescriptor(
            CapabilitySupport.Unsupported,
            FeatureAccess.None,
            Detail: "K3-family FR cancels split rather than selecting a conventional receive VFO.");
        return new RadioCapabilities(
            1,
            "Elecraft",
            profile.Model,
            "rig2cast.drivers.elecraft.k3family",
            "0.1.0",
            new VfoCapability(new HashSet<VfoId> { VfoId.A, VfoId.B }, unsupported, readWrite),
            new FrequencyCapability(
                readWrite,
                new HashSet<VfoId> { VfoId.A, VfoId.B },
                [new FrequencyRange(100_000, 54_000_000, true, false)],
                1),
            new ModeCapability(readWrite, profile.SupportedModes),
            new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write, LeaseKinds.Transmit),
            CreateControlCapabilities(profile, optionResponse, readWrite),
            CreateSwitchCapabilities(readWrite),
            CreateChoiceCapabilities(profile, optionResponse, readWrite),
            CreateMeterCapabilities(profile, optionResponse),
            new Dictionary<string, object?>
            {
                ["elecraft.protocolFamily"] = "K3",
                ["elecraft.modelId"] = profile.ModelId,
                ["elecraft.optionResponse"] = optionResponse,
                ["elecraft.receiverTargets"] = CreateReceiverTargets(profile, optionResponse),
                ["elecraft.autoInformation.modes"] = ElecraftK3Profile.AutoInformationModes,
                ["serial.supportedBaudRates"] = ElecraftK3Profile.SupportedBaudRates,
                ["rig2cast.coverage"] = "core-vfo-mode-split-ptt"
            })
        {
            Passband = CreatePassbandCapability(profile, optionResponse, readWrite)
        };
    }

    private int GetControlMaximum(RadioControlId control) => control switch
    {
        RadioControlId.AfGain => 255,
        RadioControlId.RfGain => 250,
        RadioControlId.TransmitPower => GetPowerMaximum(_profile, _optionResponse),
        RadioControlId.KeyerSpeedWpm => 50,
        _ => throw new NotSupportedException($"Control '{control}' is not supported by the Elecraft K3-family driver.")
    };

    private string DecodeAttenuator(string response)
    {
        if (_profile.ModelId == ElecraftK3Profile.K3SModelId)
            return response switch
            {
                "RA00;" => "off", "RA05;" => "5db", "RA10;" => "10db", "RA15;" => "15db",
                "RA01;" => "10db",
                _ => throw new ElecraftProtocolException($"Invalid attenuator response '{response}'.")
            };
        return response switch
        {
            "RA00;" => "off", "RA01;" => "10db",
            _ => throw new ElecraftProtocolException($"Invalid attenuator response '{response}'.")
        };
    }

    private string EncodeAttenuator(string value)
    {
        string normalized = value.ToLowerInvariant();
        if (_profile.ModelId == ElecraftK3Profile.K3SModelId)
            return normalized switch
            {
                "off" => "RA00", "5db" => "RA05", "10db" => "RA10", "15db" => "RA15",
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            };
        return normalized switch
        {
            "off" => "RA00", "10db" => "RA01",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private bool SupportsPreamp2 =>
        (_profile.ModelId is ElecraftK3Profile.K3SModelId or ElecraftK3Profile.K3ModelId) &&
        _optionResponse.AsSpan(2, _optionResponse.Length - 3).Contains('L');

    private bool IsK3Desktop =>
        _profile.ModelId is ElecraftK3Profile.K3SModelId or ElecraftK3Profile.K3ModelId;

    private bool HasSecondaryReceiver => SupportsSecondaryReceiver(_profile, _optionResponse);

    private void EnsureTargetSupported(VfoId target)
    {
        if (target == VfoId.A)
            return;
        if (target == VfoId.B && HasSecondaryReceiver)
            return;
        throw new NotSupportedException($"Receiver target '{target}' is not available on the {_profile.Model} configuration.");
    }

    private static bool SupportsSecondaryReceiver(ElecraftK3Profile profile, string optionResponse) =>
        (profile.ModelId is ElecraftK3Profile.K3SModelId or ElecraftK3Profile.K3ModelId) &&
        optionResponse.Length > 3 && optionResponse.AsSpan(2, optionResponse.Length - 3).Contains('S');

    private static HashSet<VfoId> CreateReceiverTargets(ElecraftK3Profile profile, string optionResponse) =>
        SupportsSecondaryReceiver(profile, optionResponse)
            ? new HashSet<VfoId> { VfoId.A, VfoId.B }
            : new HashSet<VfoId> { VfoId.A };

    private static string DecodeSubAttenuator(string response) => response switch
    {
        "RA$00;" => "off", "RA$01;" or "RA$10;" => "10db",
        _ => throw new ElecraftProtocolException($"Invalid sub-receiver attenuator response '{response}'.")
    };

    private static int ParseUnsignedControl(string response, string prefix, int digits, int maximum)
    {
        if (response.Length != prefix.Length + digits + 1 || response[^1] != ';' ||
            !response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(response.AsSpan(prefix.Length, digits), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
            value > maximum)
            throw new ElecraftProtocolException($"Invalid {prefix} response '{response}'.");
        return value;
    }

    private static int ParseSignedControl(string response, string prefix, int digits, int maximum)
    {
        int signIndex = prefix.Length;
        if (response.Length != prefix.Length + digits + 2 || response[^1] != ';' ||
            !response.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            response[signIndex] is not ('+' or '-' or ' ') ||
            !int.TryParse(response.AsSpan(signIndex + 1, digits), NumberStyles.None, CultureInfo.InvariantCulture, out int magnitude) ||
            magnitude > maximum)
            throw new ElecraftProtocolException($"Invalid {prefix} response '{response}'.");
        return response[signIndex] == '-' ? -magnitude : magnitude;
    }

    private static Dictionary<RadioControlId, NumericControlDescriptor> CreateControlCapabilities(
        ElecraftK3Profile profile, string optionResponse, FeatureDescriptor feature)
    {
        HashSet<VfoId> targets = CreateReceiverTargets(profile, optionResponse);
        return new()
    {
        [RadioControlId.AfGain] = new(RadioControlId.AfGain, "AF gain", feature, 0, 255, 1, "raw") { Targets = targets },
        [RadioControlId.RfGain] = new(RadioControlId.RfGain, "RF gain", feature, 0, 250, 1, "raw") { Targets = targets },
        [RadioControlId.TransmitPower] = new(
            RadioControlId.TransmitPower, "Requested transmit power", feature, 0,
            GetPowerMaximum(profile, optionResponse), 1, "W"),
        [RadioControlId.ClarifierOffsetHz] = new(
            RadioControlId.ClarifierOffsetHz, "RIT/XIT offset", feature, -9_999, 9_999, 1, "Hz"),
        [RadioControlId.KeyerSpeedWpm] = new(
            RadioControlId.KeyerSpeedWpm, "Keyer speed", feature, 8, 50, 1, "WPM")
    };
    }

    private static Dictionary<RadioSwitchId, SwitchControlDescriptor> CreateSwitchCapabilities(FeatureDescriptor feature) => new()
    {
        [RadioSwitchId.ReceiveClarifier] = new(RadioSwitchId.ReceiveClarifier, "RIT", feature),
        [RadioSwitchId.TransmitClarifier] = new(RadioSwitchId.TransmitClarifier, "XIT", feature)
    };

    private static Dictionary<RadioChoiceId, ChoiceControlDescriptor> CreateChoiceCapabilities(
        ElecraftK3Profile profile, string optionResponse, FeatureDescriptor feature)
    {
        HashSet<VfoId> targets = CreateReceiverTargets(profile, optionResponse);
        var subAttenuatorOptions = new Dictionary<string, RadioChoiceOption>
        {
            ["off"] = new("off", "Off"), ["10db"] = new("10db", "10 dB")
        };
        var subPreampOptions = new Dictionary<string, RadioChoiceOption>
        {
            ["off"] = new("off", "Off"), ["preamp1"] = new("preamp1", "Preamp 1")
        };
        var choices = new Dictionary<RadioChoiceId, ChoiceControlDescriptor>
        {
            [RadioChoiceId.Agc] = new(RadioChoiceId.Agc, "AGC speed", feature, new Dictionary<string, RadioChoiceOption>
            {
                ["fast"] = new("fast", "Fast"), ["slow"] = new("slow", "Slow")
            }),
            [RadioChoiceId.Attenuator] = new(RadioChoiceId.Attenuator, "Attenuator", feature,
                CreateAttenuatorOptions(profile))
            {
                Targets = targets,
                OptionsByTarget = new Dictionary<VfoId, IReadOnlyDictionary<string, RadioChoiceOption>>
                {
                    [VfoId.A] = CreateAttenuatorOptions(profile), [VfoId.B] = subAttenuatorOptions
                }.Where(pair => targets.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)
            },
            [RadioChoiceId.Preamp] = new(RadioChoiceId.Preamp, "Preamplifier", feature,
                CreatePreampOptions(profile, optionResponse))
            {
                Targets = targets,
                OptionsByTarget = new Dictionary<VfoId, IReadOnlyDictionary<string, RadioChoiceOption>>
                {
                    [VfoId.A] = CreatePreampOptions(profile, optionResponse), [VfoId.B] = subPreampOptions
                }.Where(pair => targets.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)
            }
        };
        return choices;
    }

    private static PassbandCapability CreatePassbandCapability(
        ElecraftK3Profile profile,
        string optionResponse,
        FeatureDescriptor feature)
    {
        var constraint = new PassbandConstraint(0, 99_990, 10, RadioMayQuantize: true);
        return new PassbandCapability(
            feature,
            profile.SupportedModes.ToDictionary(mode => mode, _ => constraint))
        {
            Targets = CreateReceiverTargets(profile, optionResponse)
        };
    }

    private static Dictionary<RadioMeterId, RadioMeterDescriptor> CreateMeterCapabilities(
        ElecraftK3Profile profile, string optionResponse)
    {
        bool desktop = profile.ModelId is ElecraftK3Profile.K3SModelId or ElecraftK3Profile.K3ModelId;
        HashSet<VfoId> targets = CreateReceiverTargets(profile, optionResponse);
        var signalRanges = new Dictionary<VfoId, RadioMeterRange>
        {
            [VfoId.A] = desktop ? new(0, 140, "raw SMH") : new(0, 15, "raw SM")
        };
        if (targets.Contains(VfoId.B))
            signalRanges[VfoId.B] = new(0, 15, "raw SM$");
        return new Dictionary<RadioMeterId, RadioMeterDescriptor>
        {
            [RadioMeterId.SignalStrength] = desktop
                ? new(RadioMeterId.SignalStrength, "High-resolution S-meter", 0, 140, "raw SMH", false) { RangesByTarget = signalRanges }
                : new(RadioMeterId.SignalStrength, "S-meter", 0, 15, "raw SM", false) { RangesByTarget = signalRanges },
            [RadioMeterId.Swr] = new(RadioMeterId.Swr, "SWR", 10, 999, "0.1 SWR", false)
            {
                RangesByTarget = new Dictionary<VfoId, RadioMeterRange> { [VfoId.A] = new(10, 999, "0.1 SWR") }
            }
        };
    }

    private static Dictionary<string, RadioChoiceOption> CreateAttenuatorOptions(ElecraftK3Profile profile)
    {
        var options = new Dictionary<string, RadioChoiceOption>
        {
            ["off"] = new("off", "Off"), ["10db"] = new("10db", "10 dB")
        };
        if (profile.ModelId == ElecraftK3Profile.K3SModelId)
        {
            options["5db"] = new("5db", "5 dB");
            options["15db"] = new("15db", "15 dB");
        }
        return options;
    }

    private static Dictionary<string, RadioChoiceOption> CreatePreampOptions(
        ElecraftK3Profile profile, string optionResponse)
    {
        var options = new Dictionary<string, RadioChoiceOption>
        {
            ["off"] = new("off", "Off"), ["preamp1"] = new("preamp1", "Preamp 1")
        };
        if ((profile.ModelId is ElecraftK3Profile.K3SModelId or ElecraftK3Profile.K3ModelId) &&
            optionResponse.AsSpan(2, optionResponse.Length - 3).Contains('L'))
            options["preamp2"] = new("preamp2", "Preamp 2");
        return options;
    }

    private static bool HasPowerAmplifier(string optionResponse) =>
        optionResponse.Length > 3 && optionResponse[3] == 'P';

    private static int GetPowerMaximum(ElecraftK3Profile profile, string optionResponse) =>
        HasPowerAmplifier(optionResponse) ? 110 :
        profile.ModelId is ElecraftK3Profile.KX3ModelId or ElecraftK3Profile.KX2ModelId ? 15 : 12;

    private static readonly string[] KnownControlPrefixes =
        ["AG", "AG$", "RG", "RG$", "KS", "PC", "RO", "RT", "XT", "GT", "RA", "PA", "BW", "FW", "IS"];

    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record ElecraftInformation(
        long FrequencyHz,
        RadioMode Mode,
        VfoId ActiveVfo,
        bool IsSplit,
        bool IsTransmitting);
}
