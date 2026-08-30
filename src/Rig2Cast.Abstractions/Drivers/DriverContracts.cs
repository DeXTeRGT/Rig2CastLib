using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Abstractions.Drivers;

public sealed record RadioConnectionOptions(
    string RadioId,
    string ModelId,
    IReadOnlyDictionary<string, string> Settings);

public sealed record RadioDriverDescriptor(
    string Id,
    Version Version,
    Version ApiVersion,
    IReadOnlyList<string> SupportedModelIds);

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

    ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default);

    ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default);
}
