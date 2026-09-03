using System.Runtime.CompilerServices;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Xiegu.Protocol;
using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Drivers.Xiegu.G90;

public sealed partial class G90Driver : IRadioDriver, IRadioReceiverFrequencyDriver,
    IRadioReceiverModeDriver, IRadioControlDriver, IRadioReceiverControlDriver,
    IRadioMeterDriver, IRadioReceiverMeterDriver, IRadioSwitchDriver,
    IRadioReceiverSwitchDriver, IRadioChoiceDriver, IRadioReceiverChoiceDriver,
    IRadioObservationSource
{
    private const long MinimumFrequencyHz = 500_000;
    private const long MaximumFrequencyHz = 30_000_000;
    private readonly IRadioTransport _transport;
    private readonly CivSession _session;
    private readonly byte _radioAddress;
    private readonly byte _controllerAddress;
    private readonly TimeProvider _timeProvider;
    private readonly bool _extendedVfoSupported;
    private int _disposed;

    private G90Driver(IRadioTransport transport, CivSession session, byte radioAddress,
        byte controllerAddress, TimeProvider timeProvider, bool identityVerified,
        bool extendedVfoSupported)
    {
        _transport = transport;
        _session = session;
        _radioAddress = radioAddress;
        _controllerAddress = controllerAddress;
        _timeProvider = timeProvider;
        _extendedVfoSupported = extendedVfoSupported;
        Capabilities = CreateCapabilities(
            radioAddress, controllerAddress, identityVerified, extendedVfoSupported);
    }

    public RadioCapabilities Capabilities { get; }

    public static async ValueTask<G90Driver> OpenAsync(
        IRadioTransport transport,
        byte radioAddress = G90Profile.DefaultRadioAddress,
        byte controllerAddress = G90Profile.DefaultControllerAddress,
        TimeSpan? responseTimeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        CivSession session = new(transport, responseTimeout: responseTimeout);
        try
        {
            bool identityVerified;
            try
            {
                CivFrame identity = await session.QueryAsync(
                    new CivFrame(radioAddress, controllerAddress, [0x1D, 0x19]),
                    new byte[] { 0x1D, 0x19 }, cancellationToken).ConfigureAwait(false);
                if (identity.Message.Length != 4 ||
                    !identity.Message.Span[2..].SequenceEqual(new byte[] { 0x00, 0x90 }))
                {
                    throw new XieguProtocolException(
                        $"Expected G90/G90S identity 00 90, received {FormatFrame(identity)}.");
                }
                identityVerified = true;
            }
            catch (Exception exception) when (exception is TimeoutException or CivCommandRejectedException)
            {
                // Firmware older than 1.80 may not implement Xiegu model identification.
                // A timed-out CI-V session is terminal. Reset the physical transport before
                // awaiting its reader because SerialPort reads do not reliably observe
                // cancellation on every Windows driver.
                session = await ResetSessionAsync(
                    transport, session, responseTimeout, cancellationToken).ConfigureAwait(false);
                CivFrame frequency = await session.QueryAsync(
                    new CivFrame(radioAddress, controllerAddress, [0x03]),
                    new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false);
                _ = ParseFrequency(frequency);
                identityVerified = false;
            }

            bool extendedVfoSupported;
            try
            {
                _ = await ReadVfoSnapshotAsync(
                    session, radioAddress, controllerAddress, cancellationToken).ConfigureAwait(false);
                extendedVfoSupported = true;
            }
            catch (Exception exception) when (exception is TimeoutException or CivCommandRejectedException or XieguProtocolException)
            {
                // Some G90 firmware/configurations accept foreground/background writes but do not
                // answer the complete 25/26 query sequence. A timed-out session is terminal, so
                // restore a usable current-VFO session and advertise only what can be read safely.
                session = await ResetSessionAsync(
                    transport, session, responseTimeout, cancellationToken).ConfigureAwait(false);
                CivFrame frequency = await session.QueryAsync(
                    new CivFrame(radioAddress, controllerAddress, [0x03]),
                    new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false);
                _ = ParseFrequency(frequency);
                extendedVfoSupported = false;
            }

            return new G90Driver(transport, session, radioAddress, controllerAddress,
                timeProvider ?? TimeProvider.System, identityVerified, extendedVfoSupported);
        }
        catch
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            finally { await session.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    private static async ValueTask<CivSession> ResetSessionAsync(
        IRadioTransport transport,
        CivSession failedSession,
        TimeSpan? responseTimeout,
        CancellationToken cancellationToken)
    {
        await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        await failedSession.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new CivSession(transport, responseTimeout: responseTimeout);
    }

    public async ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (_extendedVfoSupported)
            return await ReadExtendedStateAsync(cancellationToken).ConfigureAwait(false);

        long frequency = ParseFrequency(await QueryAsync([0x03], new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false));
        RadioMode mode = ParseMode(await QueryAsync([0x04], new byte[] { 0x04 }, cancellationToken).ConfigureAwait(false));
        bool split = ParseBoolean(await QueryAsync([0x0F], new byte[] { 0x0F }, cancellationToken).ConfigureAwait(false), 0x0F, 1);
        bool transmitting = ParseBoolean(await QueryAsync([0x1C, 0x00], new byte[] { 0x1C, 0x00 }, cancellationToken).ConfigureAwait(false), 0x1C, 2);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        var path = new RadioSignalPath(ReceiverId.Main, VfoId.Current);
        return new RadioState(1, ConnectionStatus.Connected,
            new Dictionary<VfoId, long> { [VfoId.Current] = frequency }, VfoId.Current,
            mode, split, transmitting, observedAt)
        {
            TransmitVfo = VfoId.Current,
            Vfos = new Dictionary<VfoId, RadioVfoState> { [VfoId.Current] = new(VfoId.Current, frequency, mode, observedAt) },
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(ReceiverId.Main, true, VfoId.Current, frequency, mode, null, observedAt)
            },
            SelectedReceiver = ReceiverId.Main,
            TransmitReceiver = ReceiverId.Main,
            ReceivePaths = [path],
            TransmitPath = path
        };
    }

    public async ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default)
    {
        if (_extendedVfoSupported)
        {
            await SetExtendedFrequencyAsync(target, frequencyHz, cancellationToken).ConfigureAwait(false);
            return;
        }

        EnsureCurrentVfo(target);
        await SetCurrentFrequencyCoreAsync(frequencyHz, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetFrequencyAsync(ReceiverId receiver, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        if (_extendedVfoSupported)
            await SetExtendedFrequencyAsync(VfoId.Current, frequencyHz, cancellationToken).ConfigureAwait(false);
        else
            await SetCurrentFrequencyCoreAsync(frequencyHz, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!_extendedVfoSupported)
            throw new NotSupportedException("G90 VFO selection requires extended 25/26 support.");
        byte selector = vfo switch
        {
            VfoId.A => 0x00,
            VfoId.B => 0x01,
            _ => throw new NotSupportedException($"G90 active-VFO selection does not support '{vfo}'.")
        };
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x07, selector]),
            cancellationToken).ConfigureAwait(false);
        VfoFrequency readback = ParseVfoFrequency(await QueryAsync(
            [0x25, 0x00], new byte[] { 0x25 }, cancellationToken).ConfigureAwait(false));
        if (readback.ActiveVfo != vfo)
            throw new XieguProtocolException($"G90 active VFO was {readback.ActiveVfo} after requesting {vfo}.");
    }

    public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
        SetModeCoreAsync(mode, cancellationToken);

    public ValueTask SetModeAsync(ReceiverId receiver, RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return SetModeCoreAsync(mode, cancellationToken);
    }

    public async ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x0F, enabled ? (byte)1 : (byte)0]), cancellationToken).ConfigureAwait(false);
        bool readback = ParseBoolean(await QueryAsync([0x0F], new byte[] { 0x0F }, cancellationToken).ConfigureAwait(false), 0x0F, 1);
        if (readback != enabled)
            throw new XieguProtocolException($"G90 split readback was {readback} after requesting {enabled}.");
    }

    public async ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x1C, 0x00, enabled ? (byte)1 : (byte)0]), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await foreach (CivFrame frame in _session.WatchUnsolicitedFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            int dropped = _session.ConsumeDroppedUnsolicitedFrameCount();
            if (dropped > 0)
                yield return new DeliveryGapObservation(_timeProvider.GetUtcNow(), dropped);
            yield return ParseObservation(frame);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        finally { await _session.DisposeAsync().ConfigureAwait(false); }
    }

    private ValueTask<CivFrame> QueryAsync(ReadOnlySpan<byte> command, ReadOnlyMemory<byte> expectedPrefix,
        CancellationToken cancellationToken) =>
        _session.QueryAsync(new CivFrame(_radioAddress, _controllerAddress, command), expectedPrefix, cancellationToken);

    private async ValueTask SetCurrentFrequencyCoreAsync(long frequencyHz, CancellationToken cancellationToken)
    {
        EnsureActive();
        if (frequencyHz is < MinimumFrequencyHz or > MaximumFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "G90 frequency must be between 500 kHz and 30 MHz.");
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, Prepend(0x05, CivBcd.Encode(frequencyHz, 5))), cancellationToken).ConfigureAwait(false);
        long readback = ParseFrequency(await QueryAsync([0x03], new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false));
        if (readback != frequencyHz)
            throw new XieguProtocolException($"G90 frequency readback was {readback} Hz after requesting {frequencyHz} Hz.");
    }

    private async ValueTask SetModeCoreAsync(RadioMode mode, CancellationToken cancellationToken)
    {
        EnsureActive();
        (RadioMode baseMode, bool dataMode) = ToBaseMode(mode);
        if (!G90Profile.ModeMap.TryEncode(baseMode, out byte wireMode) ||
            (dataMode && !_extendedVfoSupported))
            throw new NotSupportedException($"Mode '{mode}' is not supported by the initial G90 profile.");

        if (_extendedVfoSupported)
        {
            VfoMode current = ParseVfoMode(await QueryAsync(
                [0x26, 0x00], new byte[] { 0x26 }, cancellationToken).ConfigureAwait(false));
            await _session.CommandExpectingAcknowledgementAsync(
                new CivFrame(_radioAddress, _controllerAddress,
                    [0x26, 0x00, wireMode, dataMode ? (byte)0x01 : (byte)0x00, current.Filter]),
                cancellationToken).ConfigureAwait(false);
            VfoMode extendedReadback = ParseVfoMode(await QueryAsync(
                [0x26, 0x00], new byte[] { 0x26 }, cancellationToken).ConfigureAwait(false));
            if (extendedReadback.Mode != mode)
                throw new XieguProtocolException($"G90 mode readback was {extendedReadback.Mode} after requesting {mode}.");
            return;
        }

        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x06, wireMode]), cancellationToken).ConfigureAwait(false);
        RadioMode readback = ParseMode(await QueryAsync([0x04], new byte[] { 0x04 }, cancellationToken).ConfigureAwait(false));
        if (readback != mode)
            throw new XieguProtocolException($"G90 mode readback was {readback} after requesting {mode}.");
    }

    private static (RadioMode BaseMode, bool DataMode) ToBaseMode(RadioMode mode) => mode switch
    {
        RadioMode.DataLsb => (RadioMode.Lsb, true),
        RadioMode.DataUsb => (RadioMode.Usb, true),
        _ => (mode, false)
    };

    private static long ParseFrequency(CivFrame frame)
    {
        if (frame.Message.Length != 6 || frame.Message.Span[0] != 0x03 ||
            !CivBcd.TryDecode(frame.Message.Span[1..], out long frequency) || frequency > MaximumFrequencyHz)
            throw new XieguProtocolException($"Invalid G90 frequency response {FormatFrame(frame)}.");
        return frequency;
    }

    private static RadioMode ParseMode(CivFrame frame)
    {
        if (frame.Message.Length is not (2 or 3) || frame.Message.Span[0] != 0x04 ||
            !G90Profile.ModeMap.TryDecode(frame.Message.Span[1], out RadioMode mode))
            throw new XieguProtocolException($"Invalid G90 mode response {FormatFrame(frame)}.");
        return mode;
    }

    private async ValueTask<RadioState> ReadExtendedStateAsync(CancellationToken cancellationToken)
    {
        VfoSnapshot snapshot = await ReadVfoSnapshotAsync(
            _session, _radioAddress, _controllerAddress, cancellationToken).ConfigureAwait(false);
        bool split = ParseBoolean(await QueryAsync([0x0F], new byte[] { 0x0F }, cancellationToken).ConfigureAwait(false), 0x0F, 1);
        bool transmitting = ParseBoolean(await QueryAsync([0x1C, 0x00], new byte[] { 0x1C, 0x00 }, cancellationToken).ConfigureAwait(false), 0x1C, 2);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        VfoId transmitVfo = split ? OppositeVfo(snapshot.ActiveVfo) : snapshot.ActiveVfo;

        return new RadioState(1, ConnectionStatus.Connected,
            new Dictionary<VfoId, long>
            {
                [VfoId.A] = snapshot.FrequencyA,
                [VfoId.B] = snapshot.FrequencyB
            },
            snapshot.ActiveVfo, snapshot.ActiveMode, split, transmitting, observedAt)
        {
            TransmitVfo = transmitVfo,
            Vfos = new Dictionary<VfoId, RadioVfoState>
            {
                [VfoId.A] = new(VfoId.A, snapshot.FrequencyA, snapshot.ModeA, observedAt),
                [VfoId.B] = new(VfoId.B, snapshot.FrequencyB, snapshot.ModeB, observedAt)
            },
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(ReceiverId.Main, true, snapshot.ActiveVfo,
                    snapshot.ActiveVfo == VfoId.A ? snapshot.FrequencyA : snapshot.FrequencyB,
                    snapshot.ActiveMode, null, observedAt)
            },
            SelectedReceiver = ReceiverId.Main,
            TransmitReceiver = ReceiverId.Main,
            ReceivePaths = [new RadioSignalPath(ReceiverId.Main, snapshot.ActiveVfo)],
            TransmitPath = new RadioSignalPath(ReceiverId.Main, transmitVfo)
        };
    }

    private async ValueTask SetExtendedFrequencyAsync(
        VfoId target, long frequencyHz, CancellationToken cancellationToken)
    {
        EnsureActive();
        if (frequencyHz is < MinimumFrequencyHz or > MaximumFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "G90 frequency must be between 500 kHz and 30 MHz.");

        VfoFrequency foreground = ParseVfoFrequency(await QueryAsync(
            [0x25, 0x00], new byte[] { 0x25 }, cancellationToken).ConfigureAwait(false));
        VfoId resolved = target == VfoId.Current ? foreground.ActiveVfo : target;
        if (resolved is not (VfoId.A or VfoId.B))
            throw new NotSupportedException($"G90 frequency targeting does not support VFO '{target}'.");

        byte relativeSelector = resolved == foreground.ActiveVfo ? (byte)0x00 : (byte)0x01;
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress,
                [0x25, relativeSelector, .. CivBcd.Encode(frequencyHz, 5)]), cancellationToken).ConfigureAwait(false);

        VfoFrequency readback = ParseVfoFrequency(await QueryAsync(
            [0x25, relativeSelector], new byte[] { 0x25 }, cancellationToken).ConfigureAwait(false));
        VfoId readbackTarget = relativeSelector == 0 ? readback.ActiveVfo : OppositeVfo(readback.ActiveVfo);
        if (readbackTarget != resolved || readback.FrequencyHz != frequencyHz)
            throw new XieguProtocolException(
                $"G90 {resolved} frequency readback was {readback.FrequencyHz} Hz for {readbackTarget} after requesting {frequencyHz} Hz.");
    }

    private static async ValueTask<VfoSnapshot> ReadVfoSnapshotAsync(
        CivSession session, byte radioAddress, byte controllerAddress,
        CancellationToken cancellationToken)
    {
        CivFrame QueryFrame(ReadOnlySpan<byte> message) =>
            new(radioAddress, controllerAddress, message);

        VfoFrequency foregroundFrequency = ParseVfoFrequency(await session.QueryAsync(
            QueryFrame([0x25, 0x00]), new byte[] { 0x25 }, cancellationToken).ConfigureAwait(false));
        VfoMode foregroundMode = ParseVfoMode(await session.QueryAsync(
            QueryFrame([0x26, 0x00]), new byte[] { 0x26 }, cancellationToken).ConfigureAwait(false));
        VfoFrequency backgroundFrequency = ParseVfoFrequency(await session.QueryAsync(
            QueryFrame([0x25, 0x01]), new byte[] { 0x25 }, cancellationToken).ConfigureAwait(false));
        VfoMode backgroundMode = ParseVfoMode(await session.QueryAsync(
            QueryFrame([0x26, 0x01]), new byte[] { 0x26 }, cancellationToken).ConfigureAwait(false));

        if (foregroundFrequency.ActiveVfo != foregroundMode.ActiveVfo ||
            foregroundFrequency.ActiveVfo != backgroundFrequency.ActiveVfo ||
            foregroundFrequency.ActiveVfo != backgroundMode.ActiveVfo)
        {
            throw new XieguProtocolException("G90 active VFO changed while reading the foreground/background snapshot.");
        }

        VfoId active = foregroundFrequency.ActiveVfo;
        return active == VfoId.A
            ? new(active, foregroundFrequency.FrequencyHz, backgroundFrequency.FrequencyHz,
                foregroundMode.Mode, backgroundMode.Mode)
            : new(active, backgroundFrequency.FrequencyHz, foregroundFrequency.FrequencyHz,
                backgroundMode.Mode, foregroundMode.Mode);
    }

    private static VfoFrequency ParseVfoFrequency(CivFrame frame)
    {
        if (frame.Message.Length != 7 || frame.Message.Span[0] != 0x25 ||
            !TryParseActiveVfo(frame.Message.Span[1], out VfoId activeVfo) ||
            !CivBcd.TryDecode(frame.Message.Span[2..], out long frequency) ||
            frequency > MaximumFrequencyHz)
        {
            throw new XieguProtocolException($"Invalid G90 VFO frequency response {FormatFrame(frame)}.");
        }

        return new(activeVfo, frequency);
    }

    private static VfoMode ParseVfoMode(CivFrame frame)
    {
        if (frame.Message.Length != 5 || frame.Message.Span[0] != 0x26 ||
            !TryParseActiveVfo(frame.Message.Span[1], out VfoId activeVfo) ||
            !G90Profile.ModeMap.TryDecode(frame.Message.Span[2], out RadioMode mode))
        {
            throw new XieguProtocolException($"Invalid G90 VFO mode response {FormatFrame(frame)}.");
        }

        if (frame.Message.Span[3] is not (0x00 or 0x01) ||
            frame.Message.Span[4] is < 0x01 or > 0x03)
            throw new XieguProtocolException($"Invalid G90 VFO mode response {FormatFrame(frame)}.");
        RadioMode effectiveMode = frame.Message.Span[3] == 0x01
            ? mode switch
            {
                RadioMode.Lsb => RadioMode.DataLsb,
                RadioMode.Usb => RadioMode.DataUsb,
                _ => throw new XieguProtocolException(
                    $"G90 reported DATA mode with unsupported base mode {mode}.")
            }
            : mode;
        return new(activeVfo, effectiveMode, frame.Message.Span[3], frame.Message.Span[4]);
    }

    private static bool TryParseActiveVfo(byte value, out VfoId vfo)
    {
        vfo = value switch
        {
            0x00 => VfoId.A,
            0x01 => VfoId.B,
            _ => default
        };
        return value is 0x00 or 0x01;
    }

    private static VfoId OppositeVfo(VfoId vfo) => vfo switch
    {
        VfoId.A => VfoId.B,
        VfoId.B => VfoId.A,
        _ => throw new XieguProtocolException($"G90 returned invalid active VFO '{vfo}'.")
    };

    private static bool ParseBoolean(CivFrame frame, byte command, int offset)
    {
        if (frame.Message.Length != offset + 1 || frame.Message.Span[0] != command || frame.Message.Span[offset] is not (0 or 1))
            throw new XieguProtocolException($"Invalid G90 status response {FormatFrame(frame)}.");
        return frame.Message.Span[offset] == 1;
    }

    private RadioDriverObservation ParseObservation(CivFrame frame)
    {
        DateTimeOffset at = _timeProvider.GetUtcNow();
        string raw = FormatFrame(frame);
        if (frame.Source != _radioAddress || frame.Destination != 0x00)
            return new UnknownFrameObservation(at, raw);
        if (frame.Message.Length == 6 && frame.Message.Span[0] == 0x00 &&
            CivBcd.TryDecode(frame.Message.Span[1..], out long frequency) && frequency <= MaximumFrequencyHz)
            return new ReceiverFrequencyChangedObservation(at, raw, ReceiverId.Main, frequency);
        if (frame.Message.Length is 2 or 3 && frame.Message.Span[0] == 0x01 &&
            G90Profile.ModeMap.TryDecode(frame.Message.Span[1], out RadioMode mode))
            return new ReceiverModeChangedObservation(at, raw, ReceiverId.Main, mode);
        return new UnknownFrameObservation(at, raw);
    }

    private static RadioCapabilities CreateCapabilities(
        byte radioAddress, byte controllerAddress, bool identityVerified,
        bool extendedVfoSupported)
    {
        var readWrite = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        var readOnly = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read);
        var unavailable = new FeatureDescriptor(CapabilitySupport.DriverNotImplemented, FeatureAccess.None);
        var current = new HashSet<VfoId> { VfoId.Current };
        IReadOnlySet<VfoId> vfos = extendedVfoSupported
            ? new HashSet<VfoId> { VfoId.A, VfoId.B }
            : current;
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        var range = new FrequencyRange(MinimumFrequencyHz, MaximumFrequencyHz, true, false);
        HashSet<RadioMode> modes = G90Profile.ModeMap.ValueToWire.Keys.ToHashSet();
        if (extendedVfoSupported)
        {
            modes.Add(RadioMode.DataLsb);
            modes.Add(RadioMode.DataUsb);
        }
        return new RadioCapabilities(1, "Xiegu", "G90", "rig2cast.drivers.xiegu.g90", "0.1.0",
            new VfoCapability(vfos, extendedVfoSupported ? readWrite : unavailable, readWrite),
            new FrequencyCapability(readWrite, vfos, [range], 1)
            {
                ReceiverTargets = main,
                RangesByReceiver = new Dictionary<ReceiverId, IReadOnlyList<FrequencyRange>> { [ReceiverId.Main] = [range] }
            },
            new ModeCapability(readWrite, modes)
            {
                ReceiverTargets = main,
                ValuesByReceiver = new Dictionary<ReceiverId, IReadOnlySet<RadioMode>> { [ReceiverId.Main] = modes }
            },
            readWrite,
            CreateControls(readOnly, readWrite),
            CreateSwitches(readOnly, readWrite),
            CreateChoices(readWrite),
            CreateMeters(),
            new Dictionary<string, object?>
            {
                ["icom.civAddress"] = $"{radioAddress:X2}",
                ["icom.controllerAddress"] = $"{controllerAddress:X2}",
                ["serial.supportedBaudRates"] = G90Profile.SupportedBaudRates,
                ["xiegu.civReferenceVersion"] = "1.0",
                ["xiegu.identity"] = identityVerified ? "0090" : "legacy-frequency-probe",
                ["xiegu.identityVerified"] = identityVerified,
                ["xiegu.minimumFirmwareForIdentity"] = "1.80",
                ["xiegu.extendedVfoSupported"] = extendedVfoSupported,
                ["rig2cast.validation"] = "documented-simulated-partially-hardware-validated",
                ["rig2cast.coverage"] = "frequency-ab-vfo-mode-data-levels-meters-attenuator-preamp-agc-nb-compressor-tuner-state-rit-xit-split-ptt"
            })
        { Receivers = ReceiverTopologyCapability.MainOnly(vfos) };
    }

    private static void EnsureCurrentVfo(VfoId target)
    {
        if (target != VfoId.Current) throw new NotSupportedException($"VFO '{target}' is not exposed by the initial G90 profile.");
    }

    private static void EnsureMainReceiver(ReceiverId receiver)
    {
        if (receiver != ReceiverId.Main) throw new NotSupportedException($"Receiver '{receiver}' is not exposed by the G90.");
    }

    private static byte[] Prepend(byte value, byte[] tail)
    {
        var result = new byte[tail.Length + 1]; result[0] = value; tail.CopyTo(result, 1); return result;
    }

    private static string FormatFrame(CivFrame frame) => Convert.ToHexString(CivFrameCodec.Encode(frame));
    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record VfoFrequency(VfoId ActiveVfo, long FrequencyHz);
    private sealed record VfoMode(VfoId ActiveVfo, RadioMode Mode, byte DataMode, byte Filter);
    private sealed record VfoSnapshot(
        VfoId ActiveVfo, long FrequencyA, long FrequencyB, RadioMode ModeA, RadioMode ModeB)
    {
        public RadioMode ActiveMode => ActiveVfo == VfoId.A ? ModeA : ModeB;
    }
}
