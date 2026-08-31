using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Yaesu.Ftdx10;

public sealed class Ftdx10DriverFactory : IRadioDriverFactory
{
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
            38_400)]);

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(options.ModelId, Ftdx10CatProfile.ModelId))
        {
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by this driver factory.");
        }

        return await Ftdx10Driver.OpenAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
