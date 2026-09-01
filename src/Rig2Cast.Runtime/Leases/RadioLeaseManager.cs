using Rig2Cast.Abstractions.Security;

namespace Rig2Cast.Runtime.Leases;

public sealed class RadioLeaseManager(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LeaseToken> _leases = [];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _revision;

    public LeaseSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return SnapshotCore();
            }
        }
    }

    public LeaseToken Acquire(string kind, ClientIdentity owner, TimeSpan duration)
    {
        ValidateDuration(duration);
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_leases.TryGetValue(kind, out LeaseToken? existing))
            {
                if (existing.ExpiresAt > now)
                    throw new LeaseUnavailableException(kind);
                _leases.Remove(kind);
            }

            var lease = new LeaseToken(Guid.NewGuid(), kind, owner, now + duration);
            _leases[kind] = lease;
            _revision++;
            return lease;
        }
    }

    public LeaseToken Renew(LeaseToken lease, ClientIdentity owner, TimeSpan duration)
    {
        ValidateDuration(duration);
        lock (_gate)
        {
            ValidateCore(lease, owner);
            LeaseToken renewed = lease with { ExpiresAt = _timeProvider.GetUtcNow() + duration };
            _leases[lease.Kind] = renewed;
            _revision++;
            return renewed;
        }
    }

    public void Release(LeaseToken lease, ClientIdentity owner)
    {
        lock (_gate)
        {
            ValidateCore(lease, owner);
            _leases.Remove(lease.Kind);
            _revision++;
        }
    }

    public IReadOnlyList<LeaseToken> ReleaseAll(ClientIdentity owner)
    {
        lock (_gate)
        {
            LeaseToken[] removed = _leases.Values.Where(lease => SameOwner(lease.Owner, owner)).ToArray();
            foreach (LeaseToken lease in removed)
            {
                _leases.Remove(lease.Kind);
            }

            if (removed.Length > 0)
            {
                _revision++;
            }

            return removed;
        }
    }

    public IReadOnlyList<LeaseToken> RemoveExpired()
    {
        lock (_gate)
        {
            return RemoveExpiredCore();
        }
    }

    public void Validate(LeaseToken lease, ClientIdentity owner, string requiredKind)
    {
        lock (_gate)
        {
            if (!StringComparer.Ordinal.Equals(lease.Kind, requiredKind))
            {
                throw new InvalidLeaseException($"A '{requiredKind}' lease is required.");
            }

            ValidateCore(lease, owner);
        }
    }

    private void ValidateCore(LeaseToken lease, ClientIdentity owner)
    {
        if (!_leases.TryGetValue(lease.Kind, out LeaseToken? current) ||
            current.Value != lease.Value || !SameOwner(current.Owner, owner) ||
            current.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw new InvalidLeaseException("The lease is missing, expired, superseded, or owned by another client.");
        }
    }

    private LeaseToken[] RemoveExpiredCore()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        LeaseToken[] expired = _leases.Values.Where(lease => lease.ExpiresAt <= now).ToArray();
        foreach (LeaseToken lease in expired)
        {
            _leases.Remove(lease.Kind);
        }

        if (expired.Length > 0)
        {
            _revision++;
        }

        return expired;
    }

    private LeaseSnapshot SnapshotCore()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return new LeaseSnapshot(_revision, _leases.Values.Where(lease => lease.ExpiresAt > now).ToArray());
    }

    private static bool SameOwner(ClientIdentity left, ClientIdentity right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id);

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be positive.");
        }
    }
}
