using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Security;

namespace Rig2Cast.Abstractions.Radios;

public sealed record RadioState(
    long Revision,
    ConnectionStatus Connection,
    IReadOnlyDictionary<VfoId, long> FrequenciesHz,
    VfoId ActiveVfo,
    RadioMode Mode,
    bool IsSplit,
    bool IsTransmitting,
    DateTimeOffset ObservedAt);

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
