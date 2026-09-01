using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;

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
    long? SmallestStepHz = null)
{
    public bool CanReceive(long frequencyHz) =>
        Ranges.Any(range => range.Receive &&
                            frequencyHz >= range.MinimumHz && frequencyHz <= range.MaximumHz);

    public bool CanTransmit(long frequencyHz) =>
        Ranges.Any(range => range.Transmit &&
                            frequencyHz >= range.MinimumHz && frequencyHz <= range.MaximumHz);
}

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
    IReadOnlyDictionary<RadioControlId, NumericControlDescriptor> Controls,
    IReadOnlyDictionary<RadioSwitchId, SwitchControlDescriptor> Switches,
    IReadOnlyDictionary<RadioChoiceId, ChoiceControlDescriptor> Choices,
    IReadOnlyDictionary<RadioMeterId, RadioMeterDescriptor> Meters,
    IReadOnlyDictionary<string, object?> Extensions)
{
    public PassbandCapability Passband { get; init; } = PassbandCapability.Unsupported;
}
