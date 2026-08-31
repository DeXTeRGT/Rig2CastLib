using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;

namespace Rig2Cast.Abstractions.Drivers;

public sealed record RadioConnectionOptions(
    string RadioId,
    string ModelId,
    IReadOnlyDictionary<string, string> Settings);

public enum RadioTransportKind
{
    Serial,
    Tcp,
    Usb,
    Simulator
}

public sealed record RadioModelDescriptor(
    string Id,
    string Manufacturer,
    string Model,
    IReadOnlySet<RadioTransportKind> SupportedTransports,
    IReadOnlyList<int> SupportedBaudRates,
    int? DefaultBaudRate = null);

public sealed record RadioDriverDescriptor(
    string Id,
    Version Version,
    Version ApiVersion,
    IReadOnlyList<RadioModelDescriptor> Models)
{
    public IReadOnlyList<string> SupportedModelIds => Models.Select(model => model.Id).ToArray();
}

public interface IRadioDriverFactory
{
    RadioDriverDescriptor Descriptor { get; }

    ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default);
}

public interface IRadioDriver : IAsyncDisposable
{
    RadioCapabilities Capabilities { get; }

    ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default);

    ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default);

    ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default);

    ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IRadioControlDriver
{
    ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default);
}

public interface IRadioMeterDriver
{
    ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default);
}

public interface IRadioSwitchDriver
{
    ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken = default);

    ValueTask WriteSwitchAsync(
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken = default);
}

public interface IRadioChoiceDriver
{
    ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default);
}
