using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Yaesu.Ftdx10;

public sealed class Ftdx10DriverFactory : IRadioDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public Ftdx10DriverFactory()
        : this(TimeProvider.System)
    {
    }

    public Ftdx10DriverFactory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.yaesu.ftdx10",
        new Version(0, 1, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            Ftdx10CatProfile.ModelId,
            "Yaesu",
            "FTDX10",
            new HashSet<RadioTransportKind> { RadioTransportKind.Serial, RadioTransportKind.Simulator },
            Ftdx10CatProfile.SupportedBaudRates,
            38_400,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serial.dataBits"] = "8",
                ["serial.stopBits"] = "2",
                ["serial.parity"] = "None",
                ["serial.handshake"] = "RequestToSend",
                ["serial.dtrEnable"] = "false",
                ["serial.rtsEnable"] = "false",
                ["yaesu.autoInformation"] = "false"
            })
        {
            SerialProfile = SerialConnectionProfile.Create(
                stopBits: RadioSerialStopBits.Two,
                handshake: RadioSerialHandshake.RequestToSend)
        }]);

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, Ftdx10CatProfile.ModelId))
        {
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by this driver factory.");
        }

        bool enableAutomaticInformation = options.Settings.TryGetValue("yaesu.autoInformation", out string? configured) &&
            bool.TryParse(configured, out bool enabled) && enabled;
        return await Ftdx10Driver.OpenAsync(
            transport,
            enableAutomaticInformation: enableAutomaticInformation,
            timeProvider: _timeProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
