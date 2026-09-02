using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.ExamplePlugin;

public sealed class ExampleRadioDriverFactory : IRadioDriverFactory
{
    public const string ModelId = "rig2cast.example.reference-radio";

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.example.reference-driver",
        new Version(1, 0, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            ModelId,
            "Rig2Cast",
            "Reference virtual radio",
            new HashSet<RadioTransportKind> { RadioTransportKind.Simulator },
            [],
            DefaultConnectionSettings: new Dictionary<string, string>())]);

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, ModelId))
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by this example plugin.");

        try
        {
            if (!transport.IsConnected)
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new ExampleRadioDriver(transport);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class ExampleRadioDriver : IRadioDriver
{
    private static readonly FeatureDescriptor Unsupported =
        new(CapabilitySupport.Unsupported, FeatureAccess.None);
    private static readonly FeatureDescriptor ReadOnly =
        new(CapabilitySupport.Supported, FeatureAccess.Read);
    private readonly IRadioTransport _transport;
    private int _disposed;

    public ExampleRadioDriver(IRadioTransport transport)
    {
        _transport = transport;
        Capabilities = new RadioCapabilities(
            1,
            "Rig2Cast",
            "Reference virtual radio",
            "rig2cast.example.reference-driver",
            "1.0.0",
            new VfoCapability(new HashSet<VfoId> { VfoId.A }, Unsupported, Unsupported),
            new FrequencyCapability(
                ReadOnly,
                new HashSet<VfoId> { VfoId.A },
                [new FrequencyRange(100_000, 60_000_000, true, false)]),
            new ModeCapability(ReadOnly, new HashSet<RadioMode> { RadioMode.Usb }),
            Unsupported,
            new Dictionary<RadioControlId, NumericControlDescriptor>(),
            new Dictionary<RadioSwitchId, SwitchControlDescriptor>(),
            new Dictionary<RadioChoiceId, ChoiceControlDescriptor>(),
            new Dictionary<RadioMeterId, RadioMeterDescriptor>(),
            new Dictionary<string, object?>())
        {
            Receivers = ReceiverTopologyCapability.MainOnly(new HashSet<VfoId> { VfoId.A })
        };
    }

    public RadioCapabilities Capabilities { get; }

    public ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new RadioState(
            1,
            ConnectionStatus.Connected,
            new Dictionary<VfoId, long> { [VfoId.A] = 14_200_000 },
            VfoId.A,
            RadioMode.Usb,
            false,
            false,
            observedAt)
        {
            Vfos = new Dictionary<VfoId, RadioVfoState>
            {
                [VfoId.A] = new(VfoId.A, 14_200_000, RadioMode.Usb, observedAt)
            },
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(
                    ReceiverId.Main, true, VfoId.A, 14_200_000, RadioMode.Usb, null, observedAt)
            },
            ReceivePaths = [new RadioSignalPath(ReceiverId.Main, VfoId.A)],
            TransmitReceiver = null,
            TransmitPath = null
        });
    }

    public ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default) =>
        UnsupportedOperation();

    public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
        UnsupportedOperation();

    public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
        UnsupportedOperation();

    public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UnsupportedOperation();

    public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UnsupportedOperation();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private static ValueTask UnsupportedOperation() =>
        ValueTask.FromException(new NotSupportedException("The reference plugin is read-only."));
}
