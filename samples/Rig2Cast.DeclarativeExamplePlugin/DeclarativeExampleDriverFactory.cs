using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Protocols.Declarative;

namespace Rig2Cast.DeclarativeExamplePlugin;

public sealed class DeclarativeExampleDriverFactory : IRadioDriverFactory
{
    public const string ModelId = "rig2cast.example.declarative-radio";

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.example.declarative-driver",
        new Version(1, 0, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            ModelId,
            "Rig2Cast",
            "Declarative virtual radio",
            new HashSet<RadioTransportKind> { RadioTransportKind.Simulator },
            [],
            DefaultConnectionSettings: new Dictionary<string, string>
            {
                ["secondPreamp"] = "false"
            })]);

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, ModelId))
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by this sample.");
        try
        {
            if (!transport.IsConnected)
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            bool secondPreamp = options.Settings.TryGetValue("secondPreamp", out string? configured) &&
                bool.TryParse(configured, out bool enabled) && enabled;
            return new DeclarativeExampleDriver(transport, secondPreamp);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class DeclarativeExampleDriver : IRadioDriver, IRadioMeterDriver, IRadioChoiceDriver
{
    private static readonly FeatureDescriptor Unsupported =
        new(CapabilitySupport.Unsupported, FeatureAccess.None);
    private static readonly FeatureDescriptor ReadOnly =
        new(CapabilitySupport.Supported, FeatureAccess.Read);
    private static readonly ValueMapDescriptor<char, RadioMode> Modes = new(
        "Declarative sample modes",
        new Dictionary<char, RadioMode>
        {
            ['1'] = RadioMode.Lsb,
            ['2'] = RadioMode.Usb,
            ['3'] = RadioMode.Cw,
            ['4'] = RadioMode.Fm
        });
    private static readonly AsciiQueryDescriptor SignalMeter = new(
        "Signal strength", "SM", "SM", 6,
        new NumericFieldDescriptor("Raw signal strength", 3, 0, 255));
    private static readonly ModeApplicabilityDescriptor<string> TuningSteps = new(
        "Declarative sample tuning steps",
        Modes.WireToValue.Values,
        [
            new("10hz", "10 Hz", new HashSet<RadioMode>
                { RadioMode.Lsb, RadioMode.Usb, RadioMode.Cw }),
            new("100hz", "100 Hz", Modes.WireToValue.Values.ToHashSet()),
            new("1khz", "1 kHz", new HashSet<RadioMode> { RadioMode.Fm })
        ],
        requiredValuesPerMode: 2,
        valueComparer: StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalValueSetDescriptor<bool, string, char> Preamps = new(
        "Declarative sample preamps",
        [
            new("off", '0', "Off", _ => true),
            new("preamp1", '1', "Preamp 1", _ => true),
            new("preamp2", '2', "Preamp 2", installed => installed)
        ],
        valueComparer: StringComparer.OrdinalIgnoreCase);

    private readonly IRadioTransport _transport;
    private readonly bool _secondPreamp;
    private int _disposed;

    public DeclarativeExampleDriver(IRadioTransport transport, bool secondPreamp)
    {
        _transport = transport;
        _secondPreamp = secondPreamp;
        Capabilities = CreateCapabilities();
    }

    public RadioCapabilities Capabilities { get; }

    public ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Modes.TryDecode('2', out RadioMode mode))
            return ValueTask.FromException<RadioState>(
                new InvalidOperationException("The sample's declared mode fixture is invalid."));
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new RadioState(
            1, ConnectionStatus.Connected,
            new Dictionary<VfoId, long> { [VfoId.A] = 14_200_000 },
            VfoId.A, mode, false, false, observedAt)
        {
            Vfos = new Dictionary<VfoId, RadioVfoState>
            {
                [VfoId.A] = new(VfoId.A, 14_200_000, mode, observedAt)
            },
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(
                    ReceiverId.Main, true, VfoId.A, 14_200_000, mode, null, observedAt)
            },
            ReceivePaths = [new RadioSignalPath(ReceiverId.Main, VfoId.A)],
            TransmitReceiver = null,
            TransmitPath = null
        });
    }

    public ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        if (meter != RadioMeterId.SignalStrength)
            return ValueTask.FromException<RadioMeterReading>(
                new NotSupportedException($"Meter '{meter}' is not supported by this sample."));
        const string simulatedWireResponse = "SM127;";
        if (!SignalMeter.TryParseValue(simulatedWireResponse, out int raw))
            return ValueTask.FromException<RadioMeterReading>(
                new InvalidOperationException("The sample's declared meter fixture is invalid."));
        return ValueTask.FromResult(new RadioMeterReading(
            meter, raw, raw / 255d, DateTimeOffset.UtcNow));
    }

    public ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        string value = control switch
        {
            RadioChoiceId.TuningStep when TuningSteps.TryGetValues(
                RadioMode.Usb, out IReadOnlyList<ModeValueDescriptor<string>>? steps) => steps[0].Value,
            RadioChoiceId.Preamp when Preamps.TryDecode(_secondPreamp, '0', out string? preamp) => preamp,
            _ => throw new NotSupportedException($"Choice '{control}' is not supported by this sample.")
        };
        return ValueTask.FromResult(new RadioChoiceValue(control, value, DateTimeOffset.UtcNow));
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default) => UnsupportedOperation();

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

    private RadioCapabilities CreateCapabilities()
    {
        IReadOnlyDictionary<string, RadioChoiceOption> tuningOptions = TuningSteps.Values.ToDictionary(
            item => item.Value,
            item => new RadioChoiceOption(item.Value, item.DisplayName, true, item.ApplicableModes),
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, RadioChoiceOption> preampOptions = Preamps.GetAvailable(_secondPreamp)
            .ToDictionary(
                item => item.Value,
                item => new RadioChoiceOption(item.Value, item.DisplayName),
                StringComparer.OrdinalIgnoreCase);
        return new RadioCapabilities(
            1, "Rig2Cast", "Declarative virtual radio",
            "rig2cast.example.declarative-driver", "1.0.0",
            new VfoCapability(new HashSet<VfoId> { VfoId.A }, Unsupported, Unsupported),
            new FrequencyCapability(
                ReadOnly, new HashSet<VfoId> { VfoId.A },
                [new FrequencyRange(100_000, 60_000_000, true, false)]),
            new ModeCapability(ReadOnly, Modes.WireToValue.Values.ToHashSet()),
            Unsupported,
            new Dictionary<RadioControlId, NumericControlDescriptor>(),
            new Dictionary<RadioSwitchId, SwitchControlDescriptor>(),
            new Dictionary<RadioChoiceId, ChoiceControlDescriptor>
            {
                [RadioChoiceId.TuningStep] = new(
                    RadioChoiceId.TuningStep, "VFO tuning step", ReadOnly, tuningOptions),
                [RadioChoiceId.Preamp] = new(
                    RadioChoiceId.Preamp, "Preamplifier", ReadOnly, preampOptions)
            },
            new Dictionary<RadioMeterId, RadioMeterDescriptor>
            {
                [RadioMeterId.SignalStrength] = new(
                    RadioMeterId.SignalStrength,
                    SignalMeter.DisplayName,
                    SignalMeter.ValueField.Minimum,
                    SignalMeter.ValueField.Maximum,
                    "raw", false)
            },
            new Dictionary<string, object?>
            {
                ["declarativeVocabularyVersion"] = DeclarativeDescriptorVocabulary.CurrentVersion
            })
        {
            Receivers = ReceiverTopologyCapability.MainOnly(new HashSet<VfoId> { VfoId.A })
        };
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static ValueTask UnsupportedOperation() =>
        ValueTask.FromException(new NotSupportedException("The declarative sample is read-only."));
}
