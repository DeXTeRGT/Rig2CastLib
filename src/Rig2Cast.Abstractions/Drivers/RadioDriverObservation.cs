using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Capabilities;

namespace Rig2Cast.Abstractions.Drivers;

public enum RadioDriverObservationKind
{
    FrequencyChanged,
    ActiveVfoChanged,
    ModeChanged,
    SplitChanged,
    TransmitVfoChanged,
    TransmitChanged,
    StateInformation,
    ControlChanged,
    DeliveryGap,
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
    bool? Flag = null,
    VfoId? TransmitVfo = null,
    VfoId? ActiveVfo = null,
    bool? IsSplit = null,
    bool? IsTransmitting = null,
    RadioControlValue? NumericControl = null,
    RadioSwitchValue? SwitchControl = null,
    RadioChoiceValue? ChoiceControl = null,
    RadioPassbandValue? Passband = null,
    int DroppedFrames = 0);

public interface IRadioObservationSource
{
    IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
        CancellationToken cancellationToken = default);
}
