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
    int? DefaultBaudRate = null,
    IReadOnlyDictionary<string, string>? DefaultConnectionSettings = null);

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

    /// <summary>
    /// Opens a driver and transfers ownership of <paramref name="transport"/> to it. The driver
    /// must dispose the transport both after a failed open and when the driver is disposed.
    /// </summary>
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

    ValueTask SetSplitAsync(
        bool enabled,
        VfoId transmitVfo,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException(
            "This driver does not support explicit split transmit-VFO selection."));

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

public interface IRadioPassbandDriver
{
    ValueTask<RadioPassbandValue> ReadPassbandAsync(CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(int widthHz, CancellationToken cancellationToken = default);
}

public interface IRadioTargetedControlDriver
{
    ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, VfoId target, CancellationToken cancellationToken = default);
    ValueTask WriteControlAsync(
        RadioControlId control, VfoId target, int value, CancellationToken cancellationToken = default);
}

public interface IRadioTargetedChoiceDriver
{
    ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, VfoId target, CancellationToken cancellationToken = default);
    ValueTask WriteChoiceAsync(
        RadioChoiceId control, VfoId target, string value, CancellationToken cancellationToken = default);
}

public interface IRadioTargetedPassbandDriver
{
    ValueTask<RadioPassbandValue> ReadPassbandAsync(VfoId target, CancellationToken cancellationToken = default);
    ValueTask SetPassbandAsync(VfoId target, int widthHz, CancellationToken cancellationToken = default);
}

public interface IRadioTargetedMeterDriver
{
    ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, VfoId target, CancellationToken cancellationToken = default);
}
