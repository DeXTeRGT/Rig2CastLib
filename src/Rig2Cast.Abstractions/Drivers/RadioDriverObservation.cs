using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Radios;

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

public abstract record RadioDriverObservation(DateTimeOffset ObservedAt, string RawFrame)
{
    public abstract RadioDriverObservationKind Kind { get; }
}

public sealed record FrequencyChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    VfoId Vfo,
    long FrequencyHz) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.FrequencyChanged;
}

public sealed record ActiveVfoChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    VfoId Vfo,
    VfoId? TransmitVfo = null) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.ActiveVfoChanged;
}

public sealed record ModeChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    RadioMode Mode) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.ModeChanged;
}

public sealed record SplitChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    bool IsSplit,
    VfoId? TransmitVfo = null) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.SplitChanged;
}

public sealed record TransmitVfoChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    VfoId TransmitVfo) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.TransmitVfoChanged;
}

public sealed record TransmitChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    bool IsTransmitting) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.TransmitChanged;
}

public sealed record StateInformationObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    VfoId Vfo,
    long FrequencyHz,
    RadioMode Mode,
    VfoId? ActiveVfo = null,
    VfoId? TransmitVfo = null,
    bool? IsSplit = null,
    bool? IsTransmitting = null) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.StateInformation;
}

public abstract record ControlChangedObservation(DateTimeOffset ObservedAt, string RawFrame)
    : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.ControlChanged;

    public abstract object Value { get; }
}

public sealed record NumericControlChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    RadioControlValue Control) : ControlChangedObservation(ObservedAt, RawFrame)
{
    public override object Value => Control;
}

public sealed record SwitchControlChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    RadioSwitchValue Control) : ControlChangedObservation(ObservedAt, RawFrame)
{
    public override object Value => Control;
}

public sealed record ChoiceControlChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    RadioChoiceValue Control) : ControlChangedObservation(ObservedAt, RawFrame)
{
    public override object Value => Control;
}

public sealed record PassbandChangedObservation(
    DateTimeOffset ObservedAt,
    string RawFrame,
    RadioPassbandValue Passband) : ControlChangedObservation(ObservedAt, RawFrame)
{
    public override object Value => Passband;
}

public sealed record DeliveryGapObservation(
    DateTimeOffset ObservedAt,
    int DroppedFrames) : RadioDriverObservation(ObservedAt, string.Empty)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.DeliveryGap;
}

public sealed record IgnoredFrameObservation(
    DateTimeOffset ObservedAt,
    string RawFrame) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.Ignored;
}

public sealed record UnknownFrameObservation(
    DateTimeOffset ObservedAt,
    string RawFrame) : RadioDriverObservation(ObservedAt, RawFrame)
{
    public override RadioDriverObservationKind Kind => RadioDriverObservationKind.Unknown;
}

public interface IRadioObservationSource
{
    IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
        CancellationToken cancellationToken = default);
}
