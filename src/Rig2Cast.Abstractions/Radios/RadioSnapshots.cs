using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Security;

namespace Rig2Cast.Abstractions.Radios;

public sealed record RadioVfoState(
    VfoId Vfo,
    long FrequencyHz,
    RadioMode? Mode,
    DateTimeOffset ObservedAt);

public sealed record RadioReceiverState(
    ReceiverId Receiver,
    bool? IsEnabled,
    VfoId? SelectedVfo,
    long? FrequencyHz,
    RadioMode? Mode,
    int? PassbandHz,
    DateTimeOffset ObservedAt);

/// <summary>
/// Identifies a receiver signal path and, when the radio exposes one, the
/// tuning register currently feeding that path.
/// </summary>
public readonly record struct RadioSignalPath(
    ReceiverId Receiver,
    VfoId? Vfo);

public sealed record RadioState(
    long Revision,
    ConnectionStatus Connection,
    IReadOnlyDictionary<VfoId, long> FrequenciesHz,
    VfoId ActiveVfo,
    RadioMode Mode,
    bool IsSplit,
    bool IsTransmitting,
    DateTimeOffset ObservedAt)
{
    public VfoId TransmitVfo { get; init; } = ActiveVfo;

    public IReadOnlyDictionary<VfoId, RadioVfoState> Vfos { get; init; } =
        new Dictionary<VfoId, RadioVfoState>();

    public IReadOnlyDictionary<ReceiverId, RadioReceiverState> Receivers { get; init; } =
        new Dictionary<ReceiverId, RadioReceiverState>();

    public ReceiverId SelectedReceiver { get; init; } = ReceiverId.Main;

    public ReceiverId? TransmitReceiver { get; init; } = ReceiverId.Main;

    /// <summary>
    /// Signal paths currently participating in reception. More than one path
    /// may be active on dual-watch, diversity, multi-receiver, or slice radios.
    /// </summary>
    public IReadOnlyList<RadioSignalPath> ReceivePaths { get; init; } = [];

    /// <summary>
    /// The signal path selected for transmission, when the radio reports one.
    /// This is independent of the set of active receive paths.
    /// </summary>
    public RadioSignalPath? TransmitPath { get; init; }
}

public sealed record RadioAvailability(
    long Revision,
    IReadOnlySet<VfoId> WritableVfos,
    bool CanChangeMode,
    bool CanChangeSplit,
    bool CanRequestTransmit);

public sealed record RadioSnapshot(
    RadioCapabilities Capabilities,
    RadioAvailability Availability,
    RadioState State,
    ClientAuthorization Authorization,
    LeaseSnapshot Leases);
