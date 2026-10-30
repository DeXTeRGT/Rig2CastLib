namespace Rig2Cast.WebGui.Security;

public sealed class StationSessionBusyException(string displayName) : InvalidOperationException(
    $"The station is currently in use by {displayName}.")
{
    public string DisplayName { get; } = displayName;
}

public sealed record StationSessionStatus(
    bool Available,
    bool OwnedByCurrentUser,
    string? OwnerDisplayName,
    DateTimeOffset? AcquiredAt,
    bool CanTakeOver);

public sealed record DisplacedStationSession(string ClientId, string DisplayName);
public sealed record StationConnectionLease(Guid Generation, CancellationToken Revoked);

public sealed class StationSessionCoordinator : IDisposable
{
    private readonly object _sync = new();
    private StationLease? _owner;

    public StationSessionStatus GetStatus(AuthenticatedUser? user, string clientId, bool canTakeOver)
    {
        lock (_sync)
        {
            bool current = _owner is not null && _owner.ClientId == clientId && SameUser(_owner, user);
            return new StationSessionStatus(_owner is null, current, current ? null : _owner?.DisplayName,
                _owner?.AcquiredAt, !current && _owner is not null && canTakeOver);
        }
    }

    public CancellationToken Acquire(AuthenticatedUser? user, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        lock (_sync)
        {
            if (_owner is null)
            {
                _owner = StationLease.Create(user, clientId);
                _ = ReleaseUnconfirmedAcquireAsync(_owner);
                return _owner.Revoked.Token;
            }
            if (_owner.ClientId == clientId && SameUser(_owner, user)) return _owner.Revoked.Token;
            throw new StationSessionBusyException(_owner.DisplayName);
        }
    }

    private async Task ReleaseUnconfirmedAcquireAsync(StationLease pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        lock (_sync)
        {
            if (!ReferenceEquals(_owner, pending) || pending.ConnectionGeneration != Guid.Empty) return;
            _owner = null;
            pending.Revoked.Cancel();
            pending.Revoked.Dispose();
        }
    }

    public CancellationToken RequireOwner(AuthenticatedUser? user, string clientId)
    {
        lock (_sync)
        {
            if (_owner is not null && _owner.ClientId == clientId && SameUser(_owner, user))
                return _owner.Revoked.Token;
            throw new UnauthorizedAccessException("This browser does not own the active station session.");
        }
    }

    public void RequireAvailableOrOwner(AuthenticatedUser? user, string clientId)
    {
        lock (_sync)
        {
            if (_owner is null || (_owner.ClientId == clientId && SameUser(_owner, user))) return;
            throw new StationSessionBusyException(_owner.DisplayName);
        }
    }

    public StationConnectionLease BeginPrimaryConnection(AuthenticatedUser? user, string clientId)
    {
        lock (_sync)
        {
            if (_owner is null || _owner.ClientId != clientId || !SameUser(_owner, user))
                throw new UnauthorizedAccessException("This browser does not own the active station session.");
            _owner.ConnectionGeneration = Guid.NewGuid();
            return new StationConnectionLease(_owner.ConnectionGeneration, _owner.Revoked.Token);
        }
    }

    public void ReleaseAfterDisconnect(AuthenticatedUser? user, string clientId,
        Guid generation, TimeSpan delay) => _ = ReleaseAfterDisconnectCoreAsync(user, clientId, generation, delay);

    private async Task ReleaseAfterDisconnectCoreAsync(AuthenticatedUser? user, string clientId,
        Guid generation, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_sync)
        {
            if (_owner is null || _owner.ClientId != clientId || !SameUser(_owner, user) ||
                _owner.ConnectionGeneration != generation) return;
            StationLease released = _owner;
            _owner = null;
            released.Revoked.Cancel();
            released.Revoked.Dispose();
        }
    }

    public DisplacedStationSession TakeOver(AuthenticatedUser? user, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        lock (_sync)
        {
            if (_owner is null)
            {
                _owner = StationLease.Create(user, clientId);
                _ = ReleaseUnconfirmedAcquireAsync(_owner);
                return new DisplacedStationSession(string.Empty, string.Empty);
            }
            if (_owner.ClientId == clientId && SameUser(_owner, user))
                return new DisplacedStationSession(string.Empty, string.Empty);
            StationLease displaced = _owner;
            _owner = StationLease.Create(user, clientId);
            _ = ReleaseUnconfirmedAcquireAsync(_owner);
            displaced.Revoked.Cancel();
            displaced.Revoked.Dispose();
            return new DisplacedStationSession(displaced.ClientId, displaced.DisplayName);
        }
    }

    public bool Release(AuthenticatedUser? user, string clientId)
    {
        lock (_sync)
        {
            if (_owner is null || _owner.ClientId != clientId || !SameUser(_owner, user)) return false;
            StationLease released = _owner;
            _owner = null;
            released.Revoked.Cancel();
            released.Revoked.Dispose();
            return true;
        }
    }

    private static bool SameUser(StationLease lease, AuthenticatedUser? user) =>
        lease.UserId is null ? user is null : StringComparer.Ordinal.Equals(lease.UserId, user?.Id);

    public void Dispose()
    {
        lock (_sync)
        {
            _owner?.Revoked.Cancel();
            _owner?.Revoked.Dispose();
            _owner = null;
        }
    }

    private sealed class StationLease
    {
        private StationLease(string? userId, string displayName, string clientId)
        {
            UserId = userId;
            DisplayName = displayName;
            ClientId = clientId;
            AcquiredAt = DateTimeOffset.UtcNow;
        }

        public string? UserId { get; }
        public string DisplayName { get; }
        public string ClientId { get; }
        public DateTimeOffset AcquiredAt { get; }
        public CancellationTokenSource Revoked { get; } = new();
        public Guid ConnectionGeneration { get; set; }

        public static StationLease Create(AuthenticatedUser? user, string clientId) => new(
            user?.Id,
            string.IsNullOrWhiteSpace(user?.DisplayName) ? user?.Username ?? "Station operator" : user.DisplayName,
            clientId);
    }
}
