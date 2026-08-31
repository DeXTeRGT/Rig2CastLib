using System.Collections.Concurrent;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;

namespace Rig2Cast.Simulator;

public sealed class SimulatedFtdx10Driver : IRadioDriver, IRadioControlDriver, IRadioMeterDriver, IRadioSwitchDriver, IRadioChoiceDriver
{
    private readonly object _gate = new();
    private readonly Dictionary<VfoId, long> _frequencies = new()
    {
        [VfoId.A] = 14_200_000,
        [VfoId.B] = 7_100_000
    };
    private readonly ConcurrentQueue<string> _commandLog = new();
    private readonly Dictionary<RadioControlId, int> _controls = [];
    private readonly Dictionary<RadioMeterId, int> _meters = [];
    private readonly Dictionary<RadioSwitchId, bool> _switches = [];
    private readonly Dictionary<RadioChoiceId, string> _choices = [];
    private int _activeOperations;
    private int _maxConcurrentOperations;
    private int _commandCount;
    private Exception? _nextCommandException;
    private RadioMode _mode = RadioMode.Usb;
    private VfoId _activeVfo = VfoId.A;
    private bool _split;
    private bool _ptt;
    private bool _disposed;

    public SimulatedFtdx10Driver(SimulatedRadioOptions? options = null)
    {
        Options = options ?? new SimulatedRadioOptions();
        Capabilities = CreateCapabilities();
        foreach (NumericControlDescriptor descriptor in Capabilities.Controls.Values)
        {
            _controls[descriptor.Id] = descriptor.Minimum;
        }

        foreach (RadioMeterDescriptor descriptor in Capabilities.Meters.Values)
        {
            _meters[descriptor.Id] = descriptor.RawMinimum;
        }

        foreach (RadioSwitchId id in Capabilities.Switches.Keys)
        {
            _switches[id] = false;
        }

        foreach (ChoiceControlDescriptor descriptor in Capabilities.Choices.Values)
        {
            _choices[descriptor.Id] = descriptor.Options.Keys.First();
        }
    }

    public SimulatedRadioOptions Options { get; }

    public RadioCapabilities Capabilities { get; }

    public IReadOnlyList<string> CommandLog => _commandLog.ToArray();

    public int MaximumConcurrentOperations => Volatile.Read(ref _maxConcurrentOperations);

    public async ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync("ReadState", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return new RadioState(
                1,
                ConnectionStatus.Connected,
                new Dictionary<VfoId, long>(_frequencies),
                _activeVfo,
                _mode,
                _split,
                _ptt,
                DateTimeOffset.UtcNow);
        }
    }

    public async ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"SetFrequency:{target}:{frequencyHz}", cancellationToken).ConfigureAwait(false);
        if (!Capabilities.Frequency.Targets.Contains(target))
        {
            throw new NotSupportedException($"VFO {target} is not supported by this simulated radio.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);

        lock (_gate)
        {
            _frequencies[target] = frequencyHz;
        }
    }

    public async ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"SetMode:{mode}", cancellationToken).ConfigureAwait(false);
        if (!Capabilities.Modes.Values.Contains(mode))
        {
            throw new NotSupportedException($"Mode {mode} is not supported by this simulated radio.");
        }

        lock (_gate)
        {
            _mode = mode;
        }
    }

    public async ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"SetActiveVfo:{vfo}", cancellationToken).ConfigureAwait(false);
        if (vfo is not (VfoId.A or VfoId.B))
        {
            throw new NotSupportedException($"VFO {vfo} cannot be selected on the simulated FTDX10.");
        }
        lock (_gate)
        {
            _activeVfo = vfo;
        }
    }

    public async ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"SetSplit:{enabled}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _split = enabled;
        }
    }

    public async ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"SetPtt:{enabled}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _ptt = enabled;
        }
    }

    public async ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"ReadControl:{control}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return new RadioControlValue(control, _controls[control], DateTimeOffset.UtcNow);
        }
    }

    public async ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"WriteControl:{control}:{value}", cancellationToken).ConfigureAwait(false);
        NumericControlDescriptor descriptor = Capabilities.Controls[control];
        if (value < descriptor.Minimum || value > descriptor.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        lock (_gate)
        {
            _controls[control] = value;
        }
    }

    public async ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"ReadMeter:{meter}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            int raw = _meters[meter];
            return new RadioMeterReading(meter, raw, raw / 255d, DateTimeOffset.UtcNow);
        }
    }

    public void SetMeterValue(RadioMeterId meter, int rawValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rawValue, 255);
        lock (_gate)
        {
            _meters[meter] = rawValue;
        }
    }

    public async ValueTask<RadioSwitchValue> ReadSwitchAsync(RadioSwitchId control, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"ReadSwitch:{control}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return new RadioSwitchValue(control, _switches[control], DateTimeOffset.UtcNow);
        }
    }

    public async ValueTask WriteSwitchAsync(RadioSwitchId control, bool enabled, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"WriteSwitch:{control}:{enabled}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _switches[control] = enabled;
        }
    }

    public async ValueTask<RadioChoiceValue> ReadChoiceAsync(RadioChoiceId control, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"ReadChoice:{control}", cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return new RadioChoiceValue(control, _choices[control], DateTimeOffset.UtcNow);
        }
    }

    public async ValueTask WriteChoiceAsync(RadioChoiceId control, string value, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = await BeginOperationAsync($"WriteChoice:{control}:{value}", cancellationToken).ConfigureAwait(false);
        ChoiceControlDescriptor descriptor = Capabilities.Choices[control];
        if (!descriptor.Options.TryGetValue(value, out RadioChoiceOption? option) || !option.Writable)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        lock (_gate)
        {
            _choices[control] = value;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    public void FailNextCommand(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Interlocked.Exchange(ref _nextCommandException, exception);
    }

    private async ValueTask<IDisposable> BeginOperationAsync(string command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int active = Interlocked.Increment(ref _activeOperations);
        UpdateMaximum(active);
        try
        {
            int count = Interlocked.Increment(ref _commandCount);
            _commandLog.Enqueue(command);

            if (Options.DisconnectAfterCommandCount is int disconnectAt && count >= disconnectAt)
            {
                throw new IOException("The simulated radio disconnected.");
            }

            Exception? fault = Interlocked.Exchange(ref _nextCommandException, null);
            if (fault is not null)
            {
                throw fault;
            }

            if (Options.CommandDelay > TimeSpan.Zero)
            {
                await Task.Delay(Options.CommandDelay, cancellationToken).ConfigureAwait(false);
            }

            return new OperationLease(this);
        }
        catch
        {
            Interlocked.Decrement(ref _activeOperations);
            throw;
        }
    }

    private void UpdateMaximum(int active)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxConcurrentOperations);
            if (observed >= active)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maxConcurrentOperations, active, observed) != observed);
    }

    private static RadioCapabilities CreateCapabilities()
    {
        var readWrite = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return new RadioCapabilities(
            1,
            "Yaesu",
            "FTDX10 Simulator",
            "rig2cast.simulator.ftdx10",
            "0.1.0",
            new VfoCapability(new HashSet<VfoId> { VfoId.A, VfoId.B }, readWrite, readWrite),
            new FrequencyCapability(
                readWrite,
                new HashSet<VfoId> { VfoId.A, VfoId.B },
                [new FrequencyRange(1_800_000, 54_000_000, true, true)],
                1),
            new ModeCapability(
                readWrite,
                new HashSet<RadioMode>
                {
                    RadioMode.Lsb,
                    RadioMode.Usb,
                    RadioMode.Cw,
                    RadioMode.CwReverse,
                    RadioMode.Am,
                    RadioMode.Fm,
                    RadioMode.DataLsb,
                    RadioMode.DataUsb,
                    RadioMode.Rtty,
                    RadioMode.RttyReverse
                }),
            new FeatureDescriptor(
                CapabilitySupport.Supported,
                FeatureAccess.Read | FeatureAccess.Write,
                RequiredLease: LeaseKinds.Transmit),
            CreateControlCapabilities(),
            CreateSwitchCapabilities(),
            CreateChoiceCapabilities(),
            CreateMeterCapabilities(),
            new Dictionary<string, object?>());
    }

    private static Dictionary<RadioSwitchId, SwitchControlDescriptor> CreateSwitchCapabilities()
    {
        var feature = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return Enum.GetValues<RadioSwitchId>().ToDictionary(
            id => id,
            id => new SwitchControlDescriptor(id, id.ToString(), feature));
    }

    private static Dictionary<RadioChoiceId, ChoiceControlDescriptor> CreateChoiceCapabilities()
    {
        var feature = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return new Dictionary<RadioChoiceId, ChoiceControlDescriptor>
        {
            [RadioChoiceId.Attenuator] = CreateChoice(RadioChoiceId.Attenuator,
                ("off", "Off", true), ("6db", "6 dB", true), ("12db", "12 dB", true), ("18db", "18 dB", true)),
            [RadioChoiceId.Preamp] = CreateChoice(RadioChoiceId.Preamp,
                ("ipo", "IPO", true), ("amp1", "AMP 1", true), ("amp2", "AMP 2", true)),
            [RadioChoiceId.Agc] = CreateChoice(RadioChoiceId.Agc,
                ("off", "Off", true), ("fast", "Fast", true), ("mid", "Mid", true),
                ("slow", "Slow", true), ("auto", "Auto", true),
                ("auto-fast", "Auto (Fast)", false), ("auto-mid", "Auto (Mid)", false),
                ("auto-slow", "Auto (Slow)", false)),
            [RadioChoiceId.RoofingFilter] = CreateChoice(RadioChoiceId.RoofingFilter,
                ("12khz", "12 kHz", true), ("3khz", "3 kHz", true),
                ("500hz", "500 Hz", true), ("300hz", "300 Hz (optional)", true)),
            [RadioChoiceId.FilterWidth] = CreateFilterWidthChoice(),
            [RadioChoiceId.VoxDelay] = CreateVoxDelayChoice(),
            [RadioChoiceId.AudioPeakFilterWidth] = CreateChoice(RadioChoiceId.AudioPeakFilterWidth,
                ("narrow", "Narrow", true), ("medium", "Medium", true), ("wide", "Wide", true)),
            [RadioChoiceId.TuningStep] = CreateChoice(RadioChoiceId.TuningStep,
                ("10hz", "10 Hz", true), ("100hz", "100 Hz", true), ("1khz", "1 kHz", true))
        };

        ChoiceControlDescriptor CreateChoice(
            RadioChoiceId id,
            params (string Value, string DisplayName, bool Writable)[] values) =>
            new(id, id.ToString(), feature, values.ToDictionary(
                item => item.Value,
                item => new RadioChoiceOption(item.Value, item.DisplayName, item.Writable)));

        ChoiceControlDescriptor CreateFilterWidthChoice()
        {
            HashSet<RadioMode> ssb = [RadioMode.Lsb, RadioMode.Usb];
            HashSet<RadioMode> narrow =
                [RadioMode.Cw, RadioMode.CwReverse, RadioMode.Rtty, RadioMode.RttyReverse,
                 RadioMode.Psk, RadioMode.DataLsb, RadioMode.DataUsb];
            var options = new Dictionary<string, RadioChoiceOption>
            {
                ["default"] = new("default", "Default", true, ssb.Concat(narrow).ToHashSet())
            };
            AddWidths([300, 400, 600, 850, 1100, 1200, 1500, 1650, 1800, 1950, 2100, 2250,
                2400, 2450, 2500, 2600, 2700, 2800, 2900, 3000, 3200, 3500, 4000], ssb);
            AddWidths([50, 100, 150, 200, 250, 300, 350, 400, 450, 500, 600, 800, 1200, 1400,
                1700, 2000, 2400, 3000, 3200, 3500, 4000], narrow);
            return new ChoiceControlDescriptor(RadioChoiceId.FilterWidth, "Filter width", feature, options);

            void AddWidths(IEnumerable<int> widths, IReadOnlySet<RadioMode> modes)
            {
                foreach (int width in widths)
                {
                    string key = $"{width}hz";
                    if (options.TryGetValue(key, out RadioChoiceOption? existing))
                    {
                        options[key] = existing with
                        {
                            ApplicableModes = existing.ApplicableModes!.Concat(modes).ToHashSet()
                        };
                    }
                    else
                    {
                        options[key] = new RadioChoiceOption(key, $"{width} Hz", true, new HashSet<RadioMode>(modes));
                    }
                }
            }
        }

        ChoiceControlDescriptor CreateVoxDelayChoice()
        {
            var options = new Dictionary<string, RadioChoiceOption> { ["off"] = new("off", "Off") };
            for (int milliseconds = 100; milliseconds <= 3000; milliseconds += 100)
                options[$"{milliseconds}ms"] = new($"{milliseconds}ms", $"{milliseconds} ms");
            return new ChoiceControlDescriptor(RadioChoiceId.VoxDelay, "VOX delay", feature, options);
        }
    }

    private static Dictionary<RadioControlId, NumericControlDescriptor> CreateControlCapabilities()
    {
        var feature = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
        return Enum.GetValues<RadioControlId>().ToDictionary(
            id => id,
            id => id switch
            {
                RadioControlId.AfGain or RadioControlId.RfGain =>
                    new NumericControlDescriptor(id, id.ToString(), feature, 0, 255, 1, "raw"),
                RadioControlId.TransmitPower =>
                    new NumericControlDescriptor(id, id.ToString(), feature, 5, 100, 1, "W"),
                RadioControlId.NoiseReductionLevel =>
                    new NumericControlDescriptor(id, id.ToString(), feature, 1, 15, 1, "step"),
                RadioControlId.NoiseBlankerLevel =>
                    new NumericControlDescriptor(id, id.ToString(), feature, 0, 10, 1, "step"),
                RadioControlId.IfShiftHz =>
                    new NumericControlDescriptor(id, id.ToString(), feature, -1200, 1200, 20, "Hz"),
                RadioControlId.ManualNotchFrequencyHz =>
                    new NumericControlDescriptor(id, id.ToString(), feature, 10, 3200, 10, "Hz"),
                RadioControlId.ContourFrequencyHz =>
                    new NumericControlDescriptor(id, id.ToString(), feature, 10, 3200, 1, "Hz"),
                RadioControlId.ClarifierOffsetHz =>
                    new NumericControlDescriptor(id, id.ToString(), feature, -9999, 9999, 1, "Hz"),
                RadioControlId.CwPitchHz =>
                    new NumericControlDescriptor(id, "CW pitch", feature, 300, 1050, 10, "Hz"),
                RadioControlId.KeyerSpeedWpm =>
                    new NumericControlDescriptor(id, "Keyer speed", feature, 4, 60, 1, "WPM"),
                RadioControlId.AudioPeakFilterOffsetHz =>
                    new NumericControlDescriptor(id, "APF offset", feature, -250, 250, 10, "Hz"),
                _ => new NumericControlDescriptor(id, id.ToString(), feature, 0, 100, 1, "%")
            });
    }

    private static Dictionary<RadioMeterId, RadioMeterDescriptor> CreateMeterCapabilities() =>
        Enum.GetValues<RadioMeterId>().ToDictionary(
            id => id,
            id => new RadioMeterDescriptor(id, id.ToString(), 0, 255, "raw", false));

    private sealed class OperationLease(SimulatedFtdx10Driver owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._activeOperations);
    }
}

public sealed class SimulatedRadioOptions
{
    public TimeSpan CommandDelay { get; init; }

    public int? DisconnectAfterCommandCount { get; init; }

}
