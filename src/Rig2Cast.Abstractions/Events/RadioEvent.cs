namespace Rig2Cast.Abstractions.Events;

public enum RadioEventKind
{
    CapabilitiesChanged,
    AvailabilityChanged,
    StateChanged,
    AuthorizationChanged,
    LeaseChanged,
    ConnectionChanged,
    ControlChanged,
    Diagnostic
}

public sealed record RadioEvent(
    long Sequence,
    RadioEventKind Kind,
    DateTimeOffset OccurredAt,
    object? Payload = null);
