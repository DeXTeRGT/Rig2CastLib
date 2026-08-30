using Rig2Cast.Abstractions.Capabilities;

namespace Rig2Cast.Abstractions.Controls;

public enum RadioControlId
{
    AfGain,
    RfGain,
    Squelch,
    MicrophoneGain,
    TransmitPower,
    SpeechProcessorLevel,
    NoiseReductionLevel,
    NoiseBlankerLevel,
    MonitorLevel,
    VoxGain,
    AntiVoxLevel,
    IfShiftHz,
    ManualNotchFrequencyHz,
    ContourFrequencyHz,
    ClarifierOffsetHz
}

public sealed record NumericControlDescriptor(
    RadioControlId Id,
    string DisplayName,
    FeatureDescriptor Feature,
    int Minimum,
    int Maximum,
    int Step,
    string Unit);

public sealed record RadioControlValue(
    RadioControlId Id,
    int Value,
    DateTimeOffset ObservedAt);
