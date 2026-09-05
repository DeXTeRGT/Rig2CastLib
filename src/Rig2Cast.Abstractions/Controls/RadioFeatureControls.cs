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
    FeatureDescriptor Feature)
{
    public ModeApplicabilityDescriptor ModeApplicability { get; init; } = new();

    public IReadOnlySet<ReceiverId> ReceiverTargets { get; init; } = new HashSet<ReceiverId>();
}

public sealed record RadioSwitchValue(
    RadioSwitchId Id,
    bool Enabled,
    DateTimeOffset ObservedAt)
{
    public ReceiverId? Receiver { get; init; }
}

public enum RadioChoiceId
{
    Attenuator,
    Preamp,
    Agc,
    RoofingFilter,
    FilterWidth,
    VoxDelay,
    AudioPeakFilterWidth,
    TuningStep
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
    IReadOnlyDictionary<string, RadioChoiceOption> Options)
{
    public ModeApplicabilityDescriptor ModeApplicability { get; init; } = new();

    public IReadOnlySet<VfoId> Targets { get; init; } = new HashSet<VfoId>();

    public IReadOnlySet<ReceiverId> ReceiverTargets { get; init; } = new HashSet<ReceiverId>();

    public IReadOnlyDictionary<VfoId, IReadOnlyDictionary<string, RadioChoiceOption>>? OptionsByTarget { get; init; }

    public IReadOnlyDictionary<ReceiverId, IReadOnlyDictionary<string, RadioChoiceOption>>? OptionsByReceiver { get; init; }
}

public sealed record RadioChoiceValue(
    RadioChoiceId Id,
    string Value,
    DateTimeOffset ObservedAt,
    VfoId? Target = null)
{
    public ReceiverId? Receiver { get; init; }
}
