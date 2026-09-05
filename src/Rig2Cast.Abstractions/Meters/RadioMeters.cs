using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Capabilities;

namespace Rig2Cast.Abstractions.Meters;

public enum RadioMeterId
{
    SignalStrength,
    Compression,
    Alc,
    Power,
    Swr,
    DrainCurrent,
    DrainVoltage
}

public sealed record RadioMeterDescriptor(
    RadioMeterId Id,
    string DisplayName,
    int RawMinimum,
    int RawMaximum,
    string RawUnit,
    bool CalibrationAvailable)
{
    public ModeApplicabilityDescriptor ModeApplicability { get; init; } = new();

    public bool RequiresTransmit { get; init; }

    public IReadOnlyDictionary<VfoId, RadioMeterRange>? RangesByTarget { get; init; }

    public IReadOnlyDictionary<ReceiverId, RadioMeterRange>? RangesByReceiver { get; init; }
}

public sealed record RadioMeterRange(int RawMinimum, int RawMaximum, string RawUnit);

public sealed record RadioMeterReading(
    RadioMeterId Id,
    int RawValue,
    double NormalizedValue,
    DateTimeOffset ObservedAt,
    VfoId? Target = null)
{
    public ReceiverId? Receiver { get; init; }
}
