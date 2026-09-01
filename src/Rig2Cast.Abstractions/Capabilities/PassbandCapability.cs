using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Abstractions.Capabilities;

public sealed record PassbandConstraint(
    int MinimumHz,
    int MaximumHz,
    int StepHz,
    IReadOnlyList<int>? DiscreteValuesHz = null,
    bool RadioMayQuantize = false);

public sealed record PassbandCapability(
    FeatureDescriptor Feature,
    IReadOnlyDictionary<RadioMode, PassbandConstraint> ByMode)
{
    public IReadOnlySet<VfoId> Targets { get; init; } = new HashSet<VfoId>();

    public static PassbandCapability Unsupported { get; } = new(
        new FeatureDescriptor(CapabilitySupport.Unsupported, FeatureAccess.None),
        new Dictionary<RadioMode, PassbandConstraint>());
}

public sealed record RadioPassbandValue(int WidthHz, DateTimeOffset ObservedAt, VfoId? Target = null);
