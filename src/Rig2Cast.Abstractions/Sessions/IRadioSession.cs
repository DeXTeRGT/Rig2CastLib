using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Capabilities;

namespace Rig2Cast.Abstractions.Sessions;

public interface IRadioSession : IAsyncDisposable
{
    string RadioId { get; }

    ValueTask<RadioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    ValueTask<RadioState> RefreshStateAsync(CancellationToken cancellationToken = default);

    ValueTask<RadioState> ReadStateAsync(
        RadioReadRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RadioEvent> WatchEventsAsync(CancellationToken cancellationToken = default);

    ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default);

    ValueTask SetFrequencyAsync(
        ReceiverId receiver, long frequencyHz, CancellationToken cancellationToken = default);

    ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(
        ReceiverId receiver, RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(
        bool enabled,
        VfoId transmitVfo,
        CancellationToken cancellationToken = default);

    ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default);

    ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, VfoId target, CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control, VfoId target, int value, CancellationToken cancellationToken = default);

    ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control, ReceiverId receiver, CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control, ReceiverId receiver, int value, CancellationToken cancellationToken = default);

    ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken = default);

    ValueTask WriteSwitchAsync(
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, CancellationToken cancellationToken = default);

    ValueTask WriteSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, bool enabled, CancellationToken cancellationToken = default);

    ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default);

    ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, VfoId target, CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control, VfoId target, string value, CancellationToken cancellationToken = default);

    ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, string value, CancellationToken cancellationToken = default);

    ValueTask<RadioPassbandValue> ReadPassbandAsync(CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(int widthHz, CancellationToken cancellationToken = default);

    ValueTask<RadioPassbandValue> ReadPassbandAsync(
        VfoId target, CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(
        VfoId target, int widthHz, CancellationToken cancellationToken = default);

    ValueTask<RadioPassbandValue> ReadPassbandAsync(
        ReceiverId receiver, CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(
        ReceiverId receiver, int widthHz, CancellationToken cancellationToken = default);

    ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken = default);

    ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, VfoId target, CancellationToken cancellationToken = default);

    ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter, ReceiverId receiver, CancellationToken cancellationToken = default);

    ValueTask<LeaseToken> AcquireLeaseAsync(
        string kind,
        TimeSpan requestedDuration,
        CancellationToken cancellationToken = default);

    ValueTask<LeaseToken> RenewLeaseAsync(
        LeaseToken lease,
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

    ValueTask SetFrequencyAsync(
        ReceiverId receiver, long frequencyHz, CancellationToken cancellationToken = default);

    ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetModeAsync(
        ReceiverId receiver, RadioMode mode, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default);

    ValueTask SetSplitAsync(
        bool enabled,
        VfoId transmitVfo,
        CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control,
        int value,
        CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control, VfoId target, int value, CancellationToken cancellationToken = default);

    ValueTask WriteControlAsync(
        RadioControlId control, ReceiverId receiver, int value, CancellationToken cancellationToken = default);

    ValueTask WriteSwitchAsync(
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask WriteSwitchAsync(
        RadioSwitchId control, ReceiverId receiver, bool enabled, CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control, VfoId target, string value, CancellationToken cancellationToken = default);

    ValueTask WriteChoiceAsync(
        RadioChoiceId control, ReceiverId receiver, string value, CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(int widthHz, CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(
        VfoId target, int widthHz, CancellationToken cancellationToken = default);

    ValueTask SetPassbandAsync(
        ReceiverId receiver, int widthHz, CancellationToken cancellationToken = default);
}
