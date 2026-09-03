using System.Runtime.CompilerServices;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Icom.Protocol;
using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Drivers.Icom.Ic7300;

public sealed partial class Ic7300Driver : IRadioDriver, IRadioReceiverFrequencyDriver,
    IRadioReceiverModeDriver, IRadioPassbandDriver, IRadioReceiverPassbandDriver,
    IRadioControlDriver, IRadioReceiverControlDriver, IRadioMeterDriver,
    IRadioReceiverMeterDriver, IRadioSwitchDriver, IRadioReceiverSwitchDriver,
    IRadioChoiceDriver, IRadioReceiverChoiceDriver, IRadioObservationSource
{
    private readonly IRadioTransport _transport;
    private readonly CivSession _session;
    private readonly byte _radioAddress;
    private readonly byte _controllerAddress;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    private Ic7300Driver(
        IRadioTransport transport,
        CivSession session,
        byte radioAddress,
        byte controllerAddress,
        TimeProvider timeProvider)
    {
        _transport = transport;
        _session = session;
        _radioAddress = radioAddress;
        _controllerAddress = controllerAddress;
        _timeProvider = timeProvider;
        Capabilities = CreateCapabilities(radioAddress, controllerAddress);
    }

    public RadioCapabilities Capabilities { get; }

    public static async ValueTask<Ic7300Driver> OpenAsync(
        IRadioTransport transport,
        byte radioAddress = Ic7300Profile.DefaultRadioAddress,
        byte controllerAddress = Ic7300Profile.DefaultControllerAddress,
        TimeSpan? responseTimeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var session = new CivSession(transport, responseTimeout: responseTimeout);
        try
        {
            CivFrame identity = await session.QueryAsync(
                new CivFrame(radioAddress, controllerAddress, [0x19, 0x00]),
                new byte[] { 0x19, 0x00 },
                cancellationToken).ConfigureAwait(false);
            if (identity.Message.Length != 3 || identity.Message.Span[2] != radioAddress)
            {
                throw new IcomProtocolException(
                    $"Expected IC-7300 CI-V identity {radioAddress:X2}, received {FormatFrame(identity)}.");
            }

            return new Ic7300Driver(
                transport,
                session,
                radioAddress,
                controllerAddress,
                timeProvider ?? TimeProvider.System);
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public async ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        long frequency = ParseFrequency(await QueryAsync(
            [0x03], new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false));
        RadioMode mode = await ReadOperatingModeAsync(cancellationToken).ConfigureAwait(false);
        int? passbandHz = SupportsAdjustablePassband(mode)
            ? ParsePassband(await QueryAsync(
                [0x1A, 0x03], new byte[] { 0x1A, 0x03 }, cancellationToken).ConfigureAwait(false), mode)
            : null;
        bool split = ParseBoolean(
            await QueryAsync([0x0F], new byte[] { 0x0F }, cancellationToken).ConfigureAwait(false), 0x0F, 1);
        bool transmitting = ParseBoolean(
            await QueryAsync(
                [0x1C, 0x00], new byte[] { 0x1C, 0x00 }, cancellationToken).ConfigureAwait(false), 0x1C, 2);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        var signalPath = new RadioSignalPath(ReceiverId.Main, VfoId.Current);

        return new RadioState(
            1,
            ConnectionStatus.Connected,
            new Dictionary<VfoId, long> { [VfoId.Current] = frequency },
            VfoId.Current,
            mode,
            split,
            transmitting,
            observedAt)
        {
            TransmitVfo = VfoId.Current,
            Vfos = new Dictionary<VfoId, RadioVfoState>
            {
                [VfoId.Current] = new(VfoId.Current, frequency, mode, observedAt)
            },
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(
                    ReceiverId.Main, true, VfoId.Current, frequency, mode, passbandHz, observedAt)
            },
            SelectedReceiver = ReceiverId.Main,
            TransmitReceiver = ReceiverId.Main,
            ReceivePaths = [signalPath],
            TransmitPath = signalPath
        };
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

    public async ValueTask SetFrequencyAsync(
        VfoId target, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureCurrentVfo(target);
        await SetFrequencyCoreAsync(frequencyHz, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetFrequencyAsync(
        ReceiverId receiver, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        await SetFrequencyCoreAsync(frequencyHz, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
        UnsupportedMutation("VFO selection");

    public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
        SetModeCoreAsync(mode, cancellationToken);

    public ValueTask SetModeAsync(
        ReceiverId receiver, RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return SetModeCoreAsync(mode, cancellationToken);
    }

    public async ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x0F, enabled ? (byte)0x01 : (byte)0x00]),
            cancellationToken).ConfigureAwait(false);
        bool readback = ParseBoolean(
            await QueryAsync([0x0F], new byte[] { 0x0F }, cancellationToken).ConfigureAwait(false), 0x0F, 1);
        if (readback != enabled)
        {
            throw new IcomProtocolException(
                $"IC-7300 split readback was {readback} after requesting {enabled}.");
        }
    }

    public async ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(
                _radioAddress,
                _controllerAddress,
                [0x1C, 0x00, enabled ? (byte)0x01 : (byte)0x00]),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RadioPassbandValue> ReadPassbandAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        RadioMode mode = await ReadOperatingModeAsync(cancellationToken).ConfigureAwait(false);
        EnsureAdjustablePassband(mode);
        int widthHz = ParsePassband(await QueryAsync(
            [0x1A, 0x03], new byte[] { 0x1A, 0x03 }, cancellationToken).ConfigureAwait(false), mode);
        return new RadioPassbandValue(widthHz, _timeProvider.GetUtcNow());
    }

    public async ValueTask SetPassbandAsync(int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        RadioMode mode = await ReadOperatingModeAsync(cancellationToken).ConfigureAwait(false);
        byte code = EncodePassband(widthHz, mode);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x1A, 0x03, code]),
            cancellationToken).ConfigureAwait(false);
        int readback = ParsePassband(await QueryAsync(
            [0x1A, 0x03], new byte[] { 0x1A, 0x03 }, cancellationToken).ConfigureAwait(false), mode);
        if (readback != widthHz)
            throw new IcomProtocolException($"IC-7300 passband readback was {readback} Hz after requesting {widthHz} Hz.");
    }

    public async ValueTask<RadioPassbandValue> ReadPassbandAsync(
        ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return (await ReadPassbandAsync(cancellationToken).ConfigureAwait(false)) with { Receiver = receiver };
    }

    public ValueTask SetPassbandAsync(
        ReceiverId receiver, int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureMainReceiver(receiver);
        return SetPassbandAsync(widthHz, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ValueTask<CivFrame> QueryAsync(
        ReadOnlySpan<byte> command,
        ReadOnlyMemory<byte> expectedPrefix,
        CancellationToken cancellationToken) =>
        _session.QueryAsync(
            new CivFrame(_radioAddress, _controllerAddress, command),
            expectedPrefix,
            cancellationToken);

    private async ValueTask SetFrequencyCoreAsync(long frequencyHz, CancellationToken cancellationToken)
    {
        EnsureActive();
        if (frequencyHz is < 30_000 or > 74_800_000)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "IC-7300 receive frequency is 30 kHz to 74.8 MHz.");

        byte[] bcd = CivBcd.Encode(frequencyHz, 5);
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, Prepend(0x05, bcd)),
            cancellationToken).ConfigureAwait(false);
        long readback = ParseFrequency(await QueryAsync(
            [0x03], new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false));
        if (readback != frequencyHz)
        {
            throw new IcomProtocolException(
                $"IC-7300 frequency readback was {readback} Hz after requesting {frequencyHz} Hz.");
        }
    }

    private async ValueTask SetModeCoreAsync(RadioMode mode, CancellationToken cancellationToken)
    {
        EnsureActive();
        (RadioMode baseMode, bool dataMode) = ToBaseMode(mode);
        if (!Ic7300Profile.ModeMap.TryEncode(baseMode, out byte wireMode))
            throw new NotSupportedException($"Mode '{mode}' is not supported by the IC-7300 profile.");

        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress, [0x06, wireMode]),
            cancellationToken).ConfigureAwait(false);
        (_, byte filter) = ParseMode(await QueryAsync(
            [0x04], new byte[] { 0x04 }, cancellationToken).ConfigureAwait(false));
        await _session.CommandExpectingAcknowledgementAsync(
            new CivFrame(_radioAddress, _controllerAddress,
                dataMode ? new byte[] { 0x1A, 0x06, 0x01, filter } : new byte[] { 0x1A, 0x06, 0x00, 0x00 }),
            cancellationToken).ConfigureAwait(false);
        RadioMode readback = await ReadOperatingModeAsync(cancellationToken).ConfigureAwait(false);
        if (readback != mode)
        {
            throw new IcomProtocolException(
                $"IC-7300 mode readback was {readback} after requesting {mode}.");
        }
    }

    private async ValueTask<RadioMode> ReadOperatingModeAsync(CancellationToken cancellationToken)
    {
        (RadioMode mode, _) = ParseMode(await QueryAsync(
            [0x04], new byte[] { 0x04 }, cancellationToken).ConfigureAwait(false));
        CivFrame data = await QueryAsync(
            [0x1A, 0x06], new byte[] { 0x1A, 0x06 }, cancellationToken).ConfigureAwait(false);
        if (data.Message.Length != 4 || data.Message.Span[2] is not (0x00 or 0x01) ||
            (data.Message.Span[2] == 0x00 ? data.Message.Span[3] != 0x00 : data.Message.Span[3] is < 0x01 or > 0x03))
            throw new IcomProtocolException($"Invalid IC-7300 data-mode response {FormatFrame(data)}.");
        if (data.Message.Span[2] == 0x00)
            return mode;
        return mode switch
        {
            RadioMode.Lsb => RadioMode.DataLsb,
            RadioMode.Usb => RadioMode.DataUsb,
            RadioMode.Fm => RadioMode.DataFm,
            _ => throw new IcomProtocolException($"IC-7300 reported DATA mode with unsupported base mode {mode}.")
        };
    }

    private static (RadioMode BaseMode, bool DataMode) ToBaseMode(RadioMode mode) => mode switch
    {
        RadioMode.DataLsb => (RadioMode.Lsb, true),
        RadioMode.DataUsb => (RadioMode.Usb, true),
        RadioMode.DataFm => (RadioMode.Fm, true),
        _ => (mode, false)
    };

    private static long ParseFrequency(CivFrame frame)
    {
        if (frame.Message.Length != 6 || frame.Message.Span[0] != 0x03 ||
            !CivBcd.TryDecode(frame.Message.Span[1..], out long frequency) ||
            frequency > 74_800_000)
        {
            throw new IcomProtocolException($"Invalid IC-7300 frequency response {FormatFrame(frame)}.");
        }
        return frequency;
    }

    private static (RadioMode Mode, byte Filter) ParseMode(CivFrame frame)
    {
        if (frame.Message.Length != 3 || frame.Message.Span[0] != 0x04 ||
            !Ic7300Profile.ModeMap.TryDecode(frame.Message.Span[1], out RadioMode mode) ||
            frame.Message.Span[2] is < 0x01 or > 0x03)
        {
            throw new IcomProtocolException($"Invalid IC-7300 mode response {FormatFrame(frame)}.");
        }
        return (mode, frame.Message.Span[2]);
    }

    private static bool ParseBoolean(CivFrame frame, byte command, int valueOffset)
    {
        if (frame.Message.Length != valueOffset + 1 || frame.Message.Span[0] != command ||
            frame.Message.Span[valueOffset] is not (0x00 or 0x01))
        {
            throw new IcomProtocolException($"Invalid IC-7300 status response {FormatFrame(frame)}.");
        }
        return frame.Message.Span[valueOffset] == 0x01;
    }

    private static int ParsePassband(CivFrame frame, RadioMode mode)
    {
        if (frame.Message.Length != 3 || !frame.Message.Span[..2].SequenceEqual(new byte[] { 0x1A, 0x03 }) ||
            !CivBcd.TryDecode(frame.Message.Span[2..], out long index))
            throw new IcomProtocolException($"Invalid IC-7300 passband response {FormatFrame(frame)}.");
        int[] widths = GetPassbandWidths(mode);
        if (index >= widths.Length)
            throw new IcomProtocolException($"Invalid IC-7300 passband index {index} in {mode} mode.");
        return widths[(int)index];
    }

    private static byte EncodePassband(int widthHz, RadioMode mode)
    {
        int index = Array.IndexOf(GetPassbandWidths(mode), widthHz);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(widthHz), widthHz, $"Width is not valid in {mode} mode.");
        return CivBcd.Encode(index, 1)[0];
    }

    private static int[] GetPassbandWidths(RadioMode mode) => mode switch
    {
        RadioMode.Am => Enumerable.Range(1, 50).Select(value => value * 200).ToArray(),
        RadioMode.Lsb or RadioMode.Usb or RadioMode.DataLsb or RadioMode.DataUsb or
            RadioMode.Cw or RadioMode.CwReverse or
            RadioMode.Rtty or RadioMode.RttyReverse =>
            Enumerable.Range(1, 10).Select(value => value * 50)
                .Concat(Enumerable.Range(6, 31).Select(value => value * 100)).ToArray(),
        _ => throw new NotSupportedException($"IC-7300 adjustable passband is not available in {mode} mode.")
    };

    private static bool SupportsAdjustablePassband(RadioMode mode) =>
        mode is RadioMode.Am or RadioMode.Lsb or RadioMode.Usb or RadioMode.DataLsb or
            RadioMode.DataUsb or RadioMode.Cw or
            RadioMode.CwReverse or RadioMode.Rtty or RadioMode.RttyReverse;

    private static void EnsureAdjustablePassband(RadioMode mode)
    {
        if (!SupportsAdjustablePassband(mode))
            throw new NotSupportedException($"IC-7300 adjustable passband is not available in {mode} mode.");
    }

    private RadioDriverObservation ParseObservation(CivFrame frame)
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        string raw = FormatFrame(frame);
        try
        {
            if (frame.Source != _radioAddress || frame.Destination != 0x00)
                return new UnknownFrameObservation(observedAt, raw);
            if (frame.Message.Length == 6 && frame.Message.Span[0] == 0x00 &&
                CivBcd.TryDecode(frame.Message.Span[1..], out long frequency) && frequency <= 74_800_000)
            {
                return new ReceiverFrequencyChangedObservation(
                    observedAt, raw, ReceiverId.Main, frequency);
            }
            if (frame.Message.Length is 2 or 3 && frame.Message.Span[0] == 0x01 &&
                Ic7300Profile.ModeMap.TryDecode(frame.Message.Span[1], out RadioMode mode) &&
                (frame.Message.Length == 2 || frame.Message.Span[2] is >= 0x01 and <= 0x03))
            {
                return new ReceiverModeChangedObservation(observedAt, raw, ReceiverId.Main, mode);
            }
        }
        catch (ArgumentException)
        {
        }
        return new UnknownFrameObservation(observedAt, raw);
    }

    private static RadioCapabilities CreateCapabilities(byte radioAddress, byte controllerAddress)
    {
        var readOnly = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read);
        var readWrite = new FeatureDescriptor(
            CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        var unavailable = new FeatureDescriptor(CapabilitySupport.DriverNotImplemented, FeatureAccess.None);
        var current = new HashSet<VfoId> { VfoId.Current };
        var main = new HashSet<ReceiverId> { ReceiverId.Main };
        var receiveRange = new FrequencyRange(30_000, 74_800_000, true, false);

        return new RadioCapabilities(
            1,
            "Icom",
            "IC-7300",
            "rig2cast.drivers.icom.ic7300",
            "0.1.0",
            new VfoCapability(current, unavailable, readWrite),
            new FrequencyCapability(readWrite, current, [receiveRange], 1)
            {
                ReceiverTargets = main,
                RangesByReceiver = new Dictionary<ReceiverId, IReadOnlyList<FrequencyRange>>
                {
                    [ReceiverId.Main] = [receiveRange]
                }
            },
            new ModeCapability(readWrite, SupportedModes())
            {
                ReceiverTargets = main,
                ValuesByReceiver = new Dictionary<ReceiverId, IReadOnlySet<RadioMode>>
                {
                    [ReceiverId.Main] = SupportedModes()
                }
            },
            readWrite,
            CreateControls(readWrite),
            CreateSwitches(readWrite),
            CreateChoices(readWrite),
            CreateMeters(),
            new Dictionary<string, object?>
            {
                ["icom.civAddress"] = $"{radioAddress:X2}",
                ["icom.controllerAddress"] = $"{controllerAddress:X2}",
                ["serial.supportedBaudRates"] = Ic7300Profile.SupportedBaudRates,
                ["rig2cast.validation"] = "documented-simulated",
                ["rig2cast.coverage"] = "current-frequency-mode-data-passband-controls-meters-rit-split-ptt"
            })
        {
            Receivers = ReceiverTopologyCapability.MainOnly(current),
            Passband = CreatePassbandCapability(readWrite)
        };
    }

    private static PassbandCapability CreatePassbandCapability(FeatureDescriptor feature)
    {
        int[] narrow = GetPassbandWidths(RadioMode.Usb);
        int[] am = GetPassbandWidths(RadioMode.Am);
        var constraints = new Dictionary<RadioMode, PassbandConstraint>();
        foreach (RadioMode mode in new[]
                 {
                     RadioMode.Lsb, RadioMode.Usb, RadioMode.DataLsb, RadioMode.DataUsb,
                     RadioMode.Cw, RadioMode.CwReverse,
                     RadioMode.Rtty, RadioMode.RttyReverse
                 })
            constraints[mode] = new(50, 3_600, 50, narrow);
        constraints[RadioMode.Am] = new(200, 10_000, 200, am);
        return new PassbandCapability(feature, constraints)
        {
            Targets = new HashSet<VfoId> { VfoId.Current },
            ReceiverTargets = new HashSet<ReceiverId> { ReceiverId.Main }
        };
    }

    private static HashSet<RadioMode> SupportedModes() =>
        Ic7300Profile.ModeMap.ValueToWire.Keys
            .Concat(new[] { RadioMode.DataLsb, RadioMode.DataUsb, RadioMode.DataFm })
            .ToHashSet();

    private static ValueTask UnsupportedMutation(string operation) =>
        ValueTask.FromException(new NotSupportedException(
            $"IC-7300 {operation} mutation is not implemented by this driver."));

    private static void EnsureCurrentVfo(VfoId target)
    {
        if (target != VfoId.Current)
            throw new NotSupportedException($"VFO '{target}' is not exposed by the initial IC-7300 profile.");
    }

    private static void EnsureMainReceiver(ReceiverId receiver)
    {
        if (receiver != ReceiverId.Main)
            throw new NotSupportedException($"Receiver '{receiver}' is not exposed by the IC-7300.");
    }

    private static byte[] Prepend(byte value, byte[] tail)
    {
        var result = new byte[tail.Length + 1];
        result[0] = value;
        tail.CopyTo(result, 1);
        return result;
    }

    private static string FormatFrame(CivFrame frame) =>
        Convert.ToHexString(CivFrameCodec.Encode(frame));

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
