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
            SerialProfile = SerialConnectionProfile.Create(),
            ConnectionSettings =
            [
                new("icom.civAddress", ConnectionSettingValueType.Byte, "CI-V radio address",
                    "Destination address assigned to the transceiver.", true, "70",
                    ConnectionSettingFormat.Hexadecimal, 0, 255),
                new("icom.controllerAddress", ConnectionSettingValueType.Byte, "CI-V controller address",
                    "Source address used by Rig2Cast when communicating with the transceiver.", true, "E0",
                    ConnectionSettingFormat.Hexadecimal, 0, 255)
            ]
        }]);

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, G90Profile.ModelId))
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by the Xiegu G90 driver.");

        RadioModelDescriptor model = Descriptor.Models[0];
        ResolvedConnectionSettings settings = ConnectionSettingsResolver.ResolveForFactory(options, model);
        byte radioAddress = settings.Get<byte>("icom.civAddress");
        byte controllerAddress = settings.Get<byte>("icom.controllerAddress");
        return await G90Driver.OpenAsync(
            transport, radioAddress, controllerAddress, timeProvider: _timeProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
