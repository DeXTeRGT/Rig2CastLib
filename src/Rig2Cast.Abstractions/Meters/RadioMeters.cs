using Rig2Cast.Abstractions.Radios;

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
    public IReadOnlyDictionary<VfoId, RadioMeterRange>? RangesByTarget { get; init; }
}

public sealed record RadioMeterRange(int RawMinimum, int RawMaximum, string RawUnit);

public sealed record RadioMeterReading(
    RadioMeterId Id,
    int RawValue,
    double NormalizedValue,
    DateTimeOffset ObservedAt,
    VfoId? Target = null);
