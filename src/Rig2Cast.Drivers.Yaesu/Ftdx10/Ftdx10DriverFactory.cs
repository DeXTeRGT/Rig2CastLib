using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Yaesu.Ftdx10;

public sealed class Ftdx10DriverFactory : IRadioDriverFactory
{
    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.yaesu.ftdx10",
        new Version(0, 1, 0),
        new Version(1, 0),
        [Ftdx10CatProfile.ModelId]);

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
