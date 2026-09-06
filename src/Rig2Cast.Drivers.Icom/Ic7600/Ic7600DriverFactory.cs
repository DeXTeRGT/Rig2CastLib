using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Icom.Ic7600;

public sealed class Ic7600DriverFactory : IRadioDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public Ic7600DriverFactory() : this(TimeProvider.System) { }

    public Ic7600DriverFactory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.icom.ic7600",
        new Version(0, 1, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            Ic7600Profile.ModelId,
            "Icom",
            "IC-7600",
            new HashSet<RadioTransportKind>
            {
                RadioTransportKind.Serial, RadioTransportKind.Tcp, RadioTransportKind.Simulator
            },
            Ic7600Profile.SupportedBaudRates,
            19_200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serial.dataBits"] = "8",
                ["serial.stopBits"] = "1",
                ["serial.parity"] = "None",
                ["serial.handshake"] = "None",
                ["serial.dtrEnable"] = "false",
                ["serial.rtsEnable"] = "false",
                ["icom.civAddress"] = "7A",
                ["icom.controllerAddress"] = "E0"
            })
        {
            SerialProfile = SerialConnectionProfile.Create(),
            ConnectionSettings =
            [
                new("icom.civAddress", ConnectionSettingValueType.Byte, "CI-V radio address",
                    "Destination address assigned to the transceiver.", true, "7A",
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
        byte radioAddress;
        byte controllerAddress;
        try
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, Ic7600Profile.ModelId))
                throw new NotSupportedException($"Model '{options.ModelId}' is not supported by the Icom IC-7600 driver.");
            RadioModelDescriptor model = Descriptor.Models[0];
            ResolvedConnectionSettings settings = ConnectionSettingsResolver.ResolveForFactory(options, model);
            radioAddress = settings.Get<byte>("icom.civAddress");
            controllerAddress = settings.Get<byte>("icom.controllerAddress");
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return await Ic7600Driver.OpenAsync(
            transport, radioAddress, controllerAddress, _timeProvider, cancellationToken).ConfigureAwait(false);
    }
}
