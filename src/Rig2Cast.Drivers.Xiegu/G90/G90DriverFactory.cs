using System.Globalization;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Xiegu.G90;

public sealed class G90DriverFactory : IRadioDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public G90DriverFactory() : this(TimeProvider.System) { }

    public G90DriverFactory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.xiegu.g90",
        new Version(0, 1, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            G90Profile.ModelId,
            "Xiegu",
            "G90",
            new HashSet<RadioTransportKind> { RadioTransportKind.Serial, RadioTransportKind.Tcp, RadioTransportKind.Simulator },
            G90Profile.SupportedBaudRates,
            19_200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serial.dataBits"] = "8",
                ["serial.stopBits"] = "1",
                ["serial.parity"] = "None",
                ["serial.handshake"] = "None",
                ["serial.dtrEnable"] = "false",
                ["serial.rtsEnable"] = "false",
                ["icom.civAddress"] = "70",
                ["icom.controllerAddress"] = "E0"
            })
        {
            SerialProfile = SerialConnectionProfile.Create()
        }]);

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, G90Profile.ModelId))
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by the Xiegu G90 driver.");

        byte radioAddress = ReadHexAddress(options.Settings, "icom.civAddress", G90Profile.DefaultRadioAddress);
        byte controllerAddress = ReadHexAddress(options.Settings, "icom.controllerAddress", G90Profile.DefaultControllerAddress);
        return await G90Driver.OpenAsync(
            transport, radioAddress, controllerAddress, timeProvider: _timeProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static byte ReadHexAddress(IReadOnlyDictionary<string, string> settings, string key, byte fallback)
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
