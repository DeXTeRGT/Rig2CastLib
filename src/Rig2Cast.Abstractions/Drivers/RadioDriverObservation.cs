using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Abstractions.Drivers;

public enum RadioDriverObservationKind
{
    FrequencyChanged,
    ActiveVfoChanged,
    ModeChanged,
    SplitChanged,
    TransmitChanged,
    StateInformation,
    Ignored,
    Unknown
}

public sealed record RadioDriverObservation(
    RadioDriverObservationKind Kind,
    DateTimeOffset ObservedAt,
    string RawFrame,
    VfoId? Vfo = null,
    long? FrequencyHz = null,
    RadioMode? Mode = null,
    bool? Flag = null);

public interface IRadioObservationSource
{
    IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
        CancellationToken cancellationToken = default);
}
