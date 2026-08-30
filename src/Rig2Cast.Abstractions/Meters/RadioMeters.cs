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
    bool CalibrationAvailable);

public sealed record RadioMeterReading(
    RadioMeterId Id,
    int RawValue,
    double NormalizedValue,
    DateTimeOffset ObservedAt);
