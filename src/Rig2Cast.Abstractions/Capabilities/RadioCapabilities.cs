using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Abstractions.Capabilities;

public sealed record FrequencyRange(
    long MinimumHz,
    long MaximumHz,
    bool Receive,
    bool Transmit);

public sealed record FrequencyCapability(
    FeatureDescriptor Feature,
    IReadOnlySet<VfoId> Targets,
    IReadOnlyList<FrequencyRange> Ranges,
    long? SmallestStepHz = null);

public sealed record VfoCapability(
    IReadOnlySet<VfoId> Available,
    FeatureDescriptor Selection,
    FeatureDescriptor Split);

public sealed record ModeCapability(
    FeatureDescriptor Feature,
    IReadOnlySet<RadioMode> Values);

public sealed record RadioCapabilities(
    long Revision,
    string Manufacturer,
    string Model,
    string DriverId,
    string DriverVersion,
    VfoCapability Vfos,
    FrequencyCapability Frequency,
    ModeCapability Modes,
    FeatureDescriptor Transmit,
    IReadOnlyDictionary<string, object?> Extensions);
