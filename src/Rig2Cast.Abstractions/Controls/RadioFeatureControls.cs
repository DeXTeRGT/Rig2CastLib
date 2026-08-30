using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Abstractions.Controls;

public enum RadioSwitchId
{
    NoiseBlanker,
    NoiseReduction,
    Monitor,
    SpeechProcessor,
    Vox,
    DialLock,
    BreakIn,
    AntennaTuner,
    NarrowFilter,
    AutoNotch,
    ManualNotch,
    Contour,
    AudioPeakFilter,
    ReceiveClarifier,
    TransmitClarifier
}

public sealed record SwitchControlDescriptor(
    RadioSwitchId Id,
    string DisplayName,
    FeatureDescriptor Feature);

public sealed record RadioSwitchValue(
    RadioSwitchId Id,
    bool Enabled,
    DateTimeOffset ObservedAt);

public enum RadioChoiceId
{
    Attenuator,
    Preamp,
    Agc,
    RoofingFilter,
    FilterWidth
}

public sealed record RadioChoiceOption(
    string Value,
    string DisplayName,
    bool Writable = true,
    IReadOnlySet<RadioMode>? ApplicableModes = null);

public sealed record ChoiceControlDescriptor(
    RadioChoiceId Id,
    string DisplayName,
    FeatureDescriptor Feature,
    IReadOnlyDictionary<string, RadioChoiceOption> Options);

public sealed record RadioChoiceValue(
    RadioChoiceId Id,
    string Value,
    DateTimeOffset ObservedAt);
