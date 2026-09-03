using System.Globalization;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Icom.Ic7300;

public sealed class Ic7300DriverFactory : IRadioDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public Ic7300DriverFactory() : this(TimeProvider.System)
    {
    }

    public Ic7300DriverFactory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.icom.ic7300",
        new Version(0, 1, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            Ic7300Profile.ModelId,
            "Icom",
            "IC-7300",
            new HashSet<RadioTransportKind> { RadioTransportKind.Serial, RadioTransportKind.Simulator },
            Ic7300Profile.SupportedBaudRates,
            19_200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serial.dataBits"] = "8",
                ["serial.stopBits"] = "1",
                ["serial.parity"] = "None",
                ["serial.handshake"] = "None",
                ["serial.dtrEnable"] = "false",
                ["serial.rtsEnable"] = "false",
                ["icom.civAddress"] = "94",
                ["icom.controllerAddress"] = "E0"
            })]);

    public ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, Ic7300Profile.ModelId))
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by the Icom IC-7300 driver.");

        byte radioAddress = ReadHexAddress(options.Settings, "icom.civAddress", Ic7300Profile.DefaultRadioAddress);
        byte controllerAddress = ReadHexAddress(
            options.Settings, "icom.controllerAddress", Ic7300Profile.DefaultControllerAddress);
        return OpenCoreAsync(transport, radioAddress, controllerAddress, cancellationToken);
    }

    private async ValueTask<IRadioDriver> OpenCoreAsync(
        IRadioTransport transport,
        byte radioAddress,
        byte controllerAddress,
        CancellationToken cancellationToken) =>
        await Ic7300Driver.OpenAsync(
            transport,
            radioAddress,
            controllerAddress,
            timeProvider: _timeProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private static byte ReadHexAddress(
        IReadOnlyDictionary<string, string> settings,
        string key,
        byte fallback)
    {
        if (!settings.TryGetValue(key, out string? text))
            return fallback;
        ReadOnlySpan<char> value = text.AsSpan();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        if (!byte.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte address))
            throw new ArgumentException($"Setting '{key}' must be a CI-V hexadecimal byte address.", key);
        return address;
    }
}
