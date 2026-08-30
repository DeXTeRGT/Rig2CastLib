using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;

namespace Rig2Cast.Runtime.Sessions;

internal sealed class RadioClientSession(
    ManagedRadio radio,
    ClientAuthorization authorization) : IRadioSession
{
    private int _disposed;

    public string RadioId => radio.RadioId;

    public ValueTask<RadioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(radio.GetSnapshot(authorization));
    }

    public IAsyncEnumerable<RadioEvent> WatchEventsAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WatchEventsAsync(cancellationToken);
    }

    public ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetFrequencyAsync(authorization, target, frequencyHz, cancellationToken);
    }

    public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetModeAsync(authorization, mode, cancellationToken);
    }

    public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetSplitAsync(authorization, enabled, cancellationToken);
    }

    public ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadControlAsync(control, cancellationToken);
    }

    public ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteControlAsync(authorization, control, value, cancellationToken);
    }

    public ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadMeterAsync(meter, cancellationToken);
    }

    public ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadSwitchAsync(control, cancellationToken);
    }

    public ValueTask WriteSwitchAsync(
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteSwitchAsync(authorization, control, enabled, cancellationToken);
    }

    public ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadChoiceAsync(control, cancellationToken);
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteChoiceAsync(authorization, control, value, cancellationToken);
    }

    public ValueTask<LeaseToken> AcquireLeaseAsync(string kind, TimeSpan requestedDuration, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.AcquireLeaseAsync(authorization, kind, requestedDuration, cancellationToken);
    }

    public ValueTask<LeaseToken> RenewLeaseAsync(LeaseToken lease, TimeSpan requestedDuration, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.RenewLeaseAsync(authorization, lease, requestedDuration, cancellationToken);
    }

    public ValueTask ReleaseLeaseAsync(LeaseToken lease, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReleaseLeaseAsync(authorization, lease, cancellationToken);
    }

    public ValueTask SetPttAsync(bool enabled, LeaseToken transmitLease, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetPttAsync(authorization, enabled, transmitLease, cancellationToken);
    }

    public ValueTask ExecuteExclusiveAsync(
        Func<IRadioOperationScope, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(operation);
        return radio.ExecuteExclusiveAsync(authorization, operation, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await radio.CloseSessionAsync(authorization).ConfigureAwait(false);
        }
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
