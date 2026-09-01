using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;

namespace Rig2Cast.Runtime.Sessions;

public sealed class RenewingTransmitController : IAsyncDisposable
{
    private readonly IRadioSession _session;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _renewalInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LeaseToken? _lease;
    private CancellationTokenSource? _renewalStopping;
    private Task? _renewalTask;
    private Exception? _renewalFailure;
    private int _disposed;

    public RenewingTransmitController(
        IRadioSession session,
        TimeSpan? leaseDuration = null,
        TimeSpan? renewalInterval = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(10);
        _renewalInterval = renewalInterval ?? TimeSpan.FromSeconds(5);
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (_renewalInterval <= TimeSpan.Zero || _renewalInterval >= _leaseDuration)
            throw new ArgumentOutOfRangeException(
                nameof(renewalInterval), "Renewal interval must be positive and shorter than the lease duration.");
    }

    public Exception? RenewalFailure => Volatile.Read(ref _renewalFailure);

    public async ValueTask<RadioState> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _session.RefreshStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<RadioState> StartContinuousAsync(CancellationToken cancellationToken = default) =>
        StartAsync(_leaseDuration, renew: true, cancellationToken);

    public ValueTask<RadioState> StartForAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        return StartAsync(duration, renew: false, cancellationToken);
    }

    public async ValueTask<RadioState> StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopRenewalAsync().ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LeaseToken lease = _lease is not null && _lease.ExpiresAt > DateTimeOffset.UtcNow
                ? _lease
                : await _session.AcquireLeaseAsync(
                    LeaseKinds.Transmit, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await _session.SetPttAsync(false, lease, cancellationToken).ConfigureAwait(false);
            await _session.ReleaseLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
            _lease = null;
            return await _session.RefreshStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await StopRenewalAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lease is not null && _lease.ExpiresAt > DateTimeOffset.UtcNow)
            {
                try
                {
                    await _session.SetPttAsync(false, _lease, CancellationToken.None).ConfigureAwait(false);
                    await _session.ReleaseLeaseAsync(_lease, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidLeaseException or ObjectDisposedException)
                {
                    // Lease expiry or session disposal already triggered the runtime's safety release.
                }
            }
            _lease = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask<RadioState> StartAsync(
        TimeSpan duration,
        bool renew,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await StopRenewalAsync().ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LeaseToken lease = _lease is not null && _lease.ExpiresAt > DateTimeOffset.UtcNow
                ? await _session.RenewLeaseAsync(_lease, duration, cancellationToken).ConfigureAwait(false)
                : await _session.AcquireLeaseAsync(LeaseKinds.Transmit, duration, cancellationToken).ConfigureAwait(false);
            try
            {
                await _session.SetPttAsync(true, lease, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { await _session.ReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidLeaseException) { }
                throw;
            }
            _lease = lease;
            Volatile.Write(ref _renewalFailure, null);
            if (renew)
            {
                _renewalStopping = new CancellationTokenSource();
                _renewalTask = RenewUntilStoppedAsync(_renewalStopping.Token);
            }
            return await _session.RefreshStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RenewUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_renewalInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_lease is null)
                        return;
                    _lease = await _session.RenewLeaseAsync(
                        _lease, _leaseDuration, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _renewalFailure, exception);
        }
    }

    private async ValueTask StopRenewalAsync()
    {
        CancellationTokenSource? stopping = Interlocked.Exchange(ref _renewalStopping, null);
        Task? task = Interlocked.Exchange(ref _renewalTask, null);
        if (stopping is null)
            return;
        await stopping.CancelAsync().ConfigureAwait(false);
        if (task is not null)
            await task.ConfigureAwait(false);
        stopping.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
