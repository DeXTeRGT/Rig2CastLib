using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;

namespace Rig2Cast.Abstractions.Sessions;

public interface IRadioSession : IAsyncDisposable
{
    string RadioId { get; }

    ValueTask<RadioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<RadioEvent> WatchEventsAsync(CancellationToken cancellationToken = default);

    ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default);

    ValueTask<LeaseToken> AcquireLeaseAsync(
        string kind,
        TimeSpan requestedDuration,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseLeaseAsync(LeaseToken lease, CancellationToken cancellationToken = default);

    ValueTask SetPttAsync(bool enabled, LeaseToken transmitLease, CancellationToken cancellationToken = default);

    ValueTask ExecuteExclusiveAsync(
        Func<IRadioOperationScope, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default);
}

public interface IRadioOperationScope
{
    ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default);
}
