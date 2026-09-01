using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Capabilities;

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

    public ValueTask<RadioState> RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.RefreshStateAsync(cancellationToken);
    }

    public ValueTask<RadioState> ReadStateAsync(
        RadioReadRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadStateAsync(request, cancellationToken);
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

    public ValueTask SetFrequencyAsync(
        ReceiverId receiver, long frequencyHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetFrequencyAsync(authorization, receiver, frequencyHz, cancellationToken);
    }

    public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetActiveVfoAsync(authorization, vfo, cancellationToken);
    }

    public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetModeAsync(authorization, mode, cancellationToken);
    }

    public ValueTask SetModeAsync(
        ReceiverId receiver, RadioMode mode, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetModeAsync(authorization, receiver, mode, cancellationToken);
    }

    public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetSplitAsync(authorization, enabled, cancellationToken);
    }

    public ValueTask SetSplitAsync(
        bool enabled,
        VfoId transmitVfo,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetSplitAsync(authorization, enabled, transmitVfo, cancellationToken);
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

    public ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadControlAsync(control, target, cancellationToken);
    }

    public ValueTask WriteControlAsync(
        RadioControlId control, VfoId target, int value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteControlAsync(authorization, control, target, value, cancellationToken);
    }

    public ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadControlAsync(control, receiver, cancellationToken);
    }

    public ValueTask WriteControlAsync(
        RadioControlId control, ReceiverId receiver, int value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteControlAsync(authorization, control, receiver, value, cancellationToken);
    }

    public ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadMeterAsync(meter, cancellationToken);
    }

    public ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadMeterAsync(meter, target, cancellationToken);
    }

    public ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadMeterAsync(meter, receiver, cancellationToken);
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

    public ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadSwitchAsync(control, receiver, cancellationToken);
    }

    public ValueTask WriteSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteSwitchAsync(authorization, control, receiver, enabled, cancellationToken);
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

    public ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadChoiceAsync(control, target, cancellationToken);
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control, VfoId target, string value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteChoiceAsync(authorization, control, target, value, cancellationToken);
    }

    public ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadChoiceAsync(control, receiver, cancellationToken);
    }

    public ValueTask WriteChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, string value, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.WriteChoiceAsync(authorization, control, receiver, value, cancellationToken);
    }

    public ValueTask<RadioPassbandValue> ReadPassbandAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadPassbandAsync(cancellationToken);
    }

    public ValueTask SetPassbandAsync(int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetPassbandAsync(authorization, widthHz, cancellationToken);
    }

    public ValueTask<RadioPassbandValue> ReadPassbandAsync(
        VfoId target, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadPassbandAsync(target, cancellationToken);
    }

    public ValueTask SetPassbandAsync(
        VfoId target, int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetPassbandAsync(authorization, target, widthHz, cancellationToken);
    }

    public ValueTask<RadioPassbandValue> ReadPassbandAsync(
        ReceiverId receiver, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.ReadPassbandAsync(receiver, cancellationToken);
    }

    public ValueTask SetPassbandAsync(
        ReceiverId receiver, int widthHz, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return radio.SetPassbandAsync(authorization, receiver, widthHz, cancellationToken);
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
