using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Icom.Protocol;
using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Drivers.Icom.Ic7600;

public sealed class Ic7600Driver : IRadioDriver
{
    private readonly IRadioTransport _transport;
    private readonly CivSession _session;
    private readonly byte _radioAddress;
    private readonly byte _controllerAddress;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    private Ic7600Driver(
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

    public static async ValueTask<Ic7600Driver> OpenAsync(
        IRadioTransport transport,
        byte radioAddress = Ic7600Profile.DefaultRadioAddress,
        byte controllerAddress = Ic7600Profile.DefaultControllerAddress,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var session = new CivSession(transport);
        try
        {
            CivFrame identity = await session.QueryAsync(
                new CivFrame(radioAddress, controllerAddress, [0x19, 0x00]),
                new byte[] { 0x19, 0x00 }, cancellationToken).ConfigureAwait(false);
            if (identity.Message.Length != 3 || identity.Message.Span[2] != Ic7600Profile.ModelIdentity)
                throw new IcomProtocolException($"Expected IC-7600 identity 7A, received {FormatFrame(identity)}.");
            return new Ic7600Driver(
                transport, session, radioAddress, controllerAddress, timeProvider ?? TimeProvider.System);
        }
        catch
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            finally { await session.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public async ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        (long mainFrequency, RadioMode mainMode) = await ReadReceiverAsync(0x00, cancellationToken).ConfigureAwait(false);
        (long subFrequency, RadioMode subMode) = await ReadReceiverAsync(0x01, cancellationToken).ConfigureAwait(false);
        bool subSelected = ParseSelector(await QueryAsync([0x07, 0xD2], new byte[] { 0x07, 0xD2 }, cancellationToken));
        bool dualWatch = ParseSelector(await QueryAsync([0x07, 0xC2], new byte[] { 0x07, 0xC2 }, cancellationToken));
        bool split = ParseBoolean(await QueryAsync([0x0F], new byte[] { 0x0F }, cancellationToken), 0x0F, 1);
        bool transmitting = ParseBoolean(
            await QueryAsync([0x1C, 0x00], new byte[] { 0x1C, 0x00 }, cancellationToken), 0x1C, 2);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        ReceiverId selected = subSelected ? ReceiverId.Sub : ReceiverId.Main;
        ReceiverId transmit = split ? ReceiverId.Sub : ReceiverId.Main;

        return new RadioState(
            1, ConnectionStatus.Connected, new Dictionary<VfoId, long>(), VfoId.Current,
            selected == ReceiverId.Sub ? subMode : mainMode, split, transmitting, observedAt)
        {
            TransmitVfo = VfoId.Current,
            Vfos = new Dictionary<VfoId, RadioVfoState>(),
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(ReceiverId.Main, true, null, mainFrequency, mainMode, null, observedAt),
                [ReceiverId.Sub] = new(ReceiverId.Sub, dualWatch, null, subFrequency, subMode, null, observedAt)
            },
            SelectedReceiver = selected,
            TransmitReceiver = transmit,
            ReceivePaths = dualWatch
                ? [new(ReceiverId.Main, null), new(ReceiverId.Sub, null)]
                : [new(ReceiverId.Main, null)],
            TransmitPath = new(transmit, null)
        };
    }

    public ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default) =>
        UnsupportedMutation("frequency");
    public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
        UnsupportedMutation("VFO selection");
    public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
        UnsupportedMutation("mode");
    public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UnsupportedMutation("split");
    public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UnsupportedMutation("PTT");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        finally { await _session.DisposeAsync().ConfigureAwait(false); }
    }

    private async ValueTask<(long FrequencyHz, RadioMode Mode)> ReadReceiverAsync(
        byte receiver, CancellationToken cancellationToken)
    {
        CivFrame frequencyFrame = await QueryAsync(
            [0x25, receiver], new byte[] { 0x25, receiver }, cancellationToken).ConfigureAwait(false);
        if (frequencyFrame.Message.Length != 7 ||
            !CivBcd.TryDecode(frequencyFrame.Message.Span[2..], out long frequency) ||
            frequency is < 30_000 or > 60_000_000)
            throw new IcomProtocolException($"Invalid IC-7600 receiver frequency {FormatFrame(frequencyFrame)}.");

        CivFrame modeFrame = await QueryAsync(
            [0x26, receiver], new byte[] { 0x26, receiver }, cancellationToken).ConfigureAwait(false);
        if (modeFrame.Message.Length != 5 ||
            !Ic7600Profile.ModeMap.TryDecode(modeFrame.Message.Span[2], out RadioMode mode) ||
            modeFrame.Message.Span[3] > 0x03 || modeFrame.Message.Span[4] is < 0x01 or > 0x03)
            throw new IcomProtocolException($"Invalid IC-7600 receiver mode {FormatFrame(modeFrame)}.");
        if (modeFrame.Message.Span[3] != 0)
        {
            mode = mode switch
            {
                RadioMode.Lsb => RadioMode.DataLsb,
                RadioMode.Usb => RadioMode.DataUsb,
                RadioMode.Fm => RadioMode.DataFm,
                _ => throw new IcomProtocolException(
                    $"IC-7600 reported DATA with unsupported base mode {mode}.")
            };
        }
        return (frequency, mode);
    }

    private ValueTask<CivFrame> QueryAsync(
        ReadOnlySpan<byte> command,
        ReadOnlyMemory<byte> expectedPrefix,
        CancellationToken cancellationToken) =>
        _session.QueryAsync(new CivFrame(_radioAddress, _controllerAddress, command), expectedPrefix, cancellationToken);

    private static bool ParseSelector(CivFrame frame)
    {
        if (frame.Message.Length != 3 || frame.Message.Span[2] is not (0x00 or 0x01))
            throw new IcomProtocolException($"Invalid IC-7600 selector response {FormatFrame(frame)}.");
        return frame.Message.Span[2] == 0x01;
    }

    private static bool ParseBoolean(CivFrame frame, byte command, int valueOffset)
    {
        if (frame.Message.Length != valueOffset + 1 || frame.Message.Span[0] != command ||
            frame.Message.Span[valueOffset] is not (0x00 or 0x01))
            throw new IcomProtocolException($"Invalid IC-7600 status response {FormatFrame(frame)}.");
        return frame.Message.Span[valueOffset] == 0x01;
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static ValueTask UnsupportedMutation(string operation) =>
        ValueTask.FromException(new NotSupportedException(
            $"IC-7600 {operation} mutation is not implemented by the initial read-only driver."));

    private static RadioCapabilities CreateCapabilities(byte radioAddress, byte controllerAddress)
    {
        var readOnly = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read);
        var unavailable = new FeatureDescriptor(CapabilitySupport.DriverNotImplemented, FeatureAccess.None);
        var receivers = new HashSet<ReceiverId> { ReceiverId.Main, ReceiverId.Sub };
        var range = new FrequencyRange(30_000, 60_000_000, true, false);
        var modes = Ic7600Profile.ModeMap.ValueToWire.Keys
            .Concat([RadioMode.DataLsb, RadioMode.DataUsb, RadioMode.DataFm]).ToHashSet();

        return new RadioCapabilities(
            1, "Icom", "IC-7600", "rig2cast.drivers.icom.ic7600", "0.1.0",
            new VfoCapability(new HashSet<VfoId>(), unavailable, readOnly),
            new FrequencyCapability(readOnly, new HashSet<VfoId>(), [range], 1)
            {
                ReceiverTargets = receivers,
                RangesByReceiver = receivers.ToDictionary(
                    receiver => receiver, _ => (IReadOnlyList<FrequencyRange>)[range])
            },
            new ModeCapability(readOnly, modes)
            {
                ReceiverTargets = receivers,
                ValuesByReceiver = receivers.ToDictionary(
                    receiver => receiver, _ => (IReadOnlySet<RadioMode>)modes)
            },
            readOnly,
            new Dictionary<RadioControlId, NumericControlDescriptor>(),
            new Dictionary<RadioSwitchId, SwitchControlDescriptor>(),
            new Dictionary<RadioChoiceId, ChoiceControlDescriptor>(),
            new Dictionary<RadioMeterId, RadioMeterDescriptor>(),
            new Dictionary<string, object?>
            {
                ["icom.civAddress"] = $"{radioAddress:X2}",
                ["icom.controllerAddress"] = $"{controllerAddress:X2}",
                ["icom.minimumReceiverAwareFirmware"] = "2.00",
                ["serial.supportedBaudRates"] = Ic7600Profile.SupportedBaudRates,
                ["rig2cast.validation"] = "documented-simulated",
                ["rig2cast.coverage"] = "identity-main-sub-state-dualwatch-split-ptt-status"
            })
        {
            Receivers = new ReceiverTopologyCapability(
                new Dictionary<ReceiverId, ReceiverCapability>
                {
                    [ReceiverId.Main] = new(
                        ReceiverId.Main, "Main readout", new HashSet<VfoId>(),
                        SupportsSimultaneousReception: true, HasIndependentFrequency: true,
                        HasIndependentMode: true),
                    [ReceiverId.Sub] = new(
                        ReceiverId.Sub, "Sub readout", new HashSet<VfoId>(),
                        SupportsSimultaneousReception: true, HasIndependentFrequency: true,
                        HasIndependentMode: true)
                }, readOnly)
        };
    }

    private static string FormatFrame(CivFrame frame) =>
        $"{frame.Source:X2}->{frame.Destination:X2}:{Convert.ToHexString(frame.Message.Span)}";
}
