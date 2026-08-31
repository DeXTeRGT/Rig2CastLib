using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Runtime.Events;
using Rig2Cast.Runtime.Leases;
using Rig2Cast.Runtime.Scheduling;

namespace Rig2Cast.Runtime.Sessions;

public sealed class ManagedRadio : IAsyncDisposable
{
    private readonly IRadioDriver _driver;
    private readonly RadioCommandScheduler _scheduler;
    private readonly RadioLeaseManager _leases;
    private readonly RadioEventHub _events = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _leaseMonitor;
    private RadioState _state;
    private long _availabilityRevision = 1;
    private int _disposed;

    private ManagedRadio(
        string radioId,
        IRadioDriver driver,
        RadioCommandScheduler scheduler,
        RadioLeaseManager leases,
        RadioState initialState)
    {
        RadioId = radioId;
        _driver = driver;
        _scheduler = scheduler;
        _leases = leases;
        _state = initialState;
        _leaseMonitor = MonitorLeasesAsync();
    }

    public string RadioId { get; }

    public static async ValueTask<ManagedRadio> CreateAsync(
        string radioId,
        IRadioDriver driver,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var scheduler = new RadioCommandScheduler();
        try
        {
            RadioState state = await scheduler.ExecuteAsync(
                driver.ReadStateAsync,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ManagedRadio(radioId, driver, scheduler, new RadioLeaseManager(timeProvider), state);
        }
        catch
        {
            await scheduler.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IRadioSession OpenSession(ClientIdentity client, params ClientRole[] roles)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.Id);
        return new RadioClientSession(this, CreateAuthorization(client, roles));
    }

    internal RadioSnapshot GetSnapshot(ClientAuthorization authorization) =>
        new(_driver.Capabilities, CreateAvailability(authorization), _state, authorization, _leases.Snapshot);

    internal IAsyncEnumerable<RadioEvent> WatchEventsAsync(CancellationToken cancellationToken) =>
        _events.SubscribeAsync(cancellationToken);

    internal ValueTask<RadioState> RefreshStateAsync(CancellationToken cancellationToken) =>
        _scheduler.ExecuteAsync(RefreshStateCoreAsync, cancellationToken: cancellationToken);

    internal ValueTask SetFrequencyAsync(
        ClientAuthorization authorization,
        VfoId target,
        long frequencyHz,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            authorization,
            token => _driver.SetFrequencyAsync(target, frequencyHz, token),
            cancellationToken);

    internal ValueTask SetActiveVfoAsync(
        ClientAuthorization authorization,
        VfoId vfo,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(authorization, token => _driver.SetActiveVfoAsync(vfo, token), cancellationToken);

    internal ValueTask SetModeAsync(
        ClientAuthorization authorization,
        RadioMode mode,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(authorization, token => _driver.SetModeAsync(mode, token), cancellationToken);

    internal ValueTask SetSplitAsync(
        ClientAuthorization authorization,
        bool enabled,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(authorization, token => _driver.SetSplitAsync(enabled, token), cancellationToken);

    internal ValueTask<RadioControlValue> ReadControlAsync(
        RadioControlId control,
        CancellationToken cancellationToken)
    {
        if (!_driver.Capabilities.Controls.ContainsKey(control) || _driver is not IRadioControlDriver controlDriver)
        {
            throw new NotSupportedException($"Radio control '{control}' is not supported by this driver.");
        }

        return _scheduler.ExecuteAsync(
            token => controlDriver.ReadControlAsync(control, token),
            cancellationToken: cancellationToken);
    }

    internal async ValueTask WriteControlAsync(
        ClientAuthorization authorization,
        RadioControlId control,
        int value,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        if (!_driver.Capabilities.Controls.TryGetValue(control, out NumericControlDescriptor? descriptor) ||
            _driver is not IRadioControlDriver controlDriver)
        {
            throw new NotSupportedException($"Radio control '{control}' is not supported by this driver.");
        }

        if (value < descriptor.Minimum || value > descriptor.Maximum ||
            (value - descriptor.Minimum) % descriptor.Step != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        await _scheduler.ExecuteAsync(async token =>
        {
            await controlDriver.WriteControlAsync(control, value, token).ConfigureAwait(false);
            RadioControlValue confirmed = await controlDriver.ReadControlAsync(control, token).ConfigureAwait(false);
            _events.Publish(RadioEventKind.ControlChanged, confirmed);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken)
    {
        if (!_driver.Capabilities.Meters.ContainsKey(meter) || _driver is not IRadioMeterDriver meterDriver)
        {
            throw new NotSupportedException($"Radio meter '{meter}' is not supported by this driver.");
        }

        return _scheduler.ExecuteAsync(
            token => meterDriver.ReadMeterAsync(meter, token),
            cancellationToken: cancellationToken);
    }

    internal ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken)
    {
        if (!_driver.Capabilities.Switches.ContainsKey(control) || _driver is not IRadioSwitchDriver switchDriver)
        {
            throw new NotSupportedException($"Radio switch '{control}' is not supported by this driver.");
        }

        return _scheduler.ExecuteAsync(
            token => switchDriver.ReadSwitchAsync(control, token),
            cancellationToken: cancellationToken);
    }

    internal async ValueTask WriteSwitchAsync(
        ClientAuthorization authorization,
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        if (!_driver.Capabilities.Switches.ContainsKey(control) || _driver is not IRadioSwitchDriver switchDriver)
        {
            throw new NotSupportedException($"Radio switch '{control}' is not supported by this driver.");
        }

        await _scheduler.ExecuteAsync(async token =>
        {
            await switchDriver.WriteSwitchAsync(control, enabled, token).ConfigureAwait(false);
            RadioSwitchValue confirmed = await switchDriver.ReadSwitchAsync(control, token).ConfigureAwait(false);
            _events.Publish(RadioEventKind.ControlChanged, confirmed);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken)
    {
        if (!_driver.Capabilities.Choices.ContainsKey(control) || _driver is not IRadioChoiceDriver choiceDriver)
        {
            throw new NotSupportedException($"Radio choice '{control}' is not supported by this driver.");
        }

        return _scheduler.ExecuteAsync(
            token => choiceDriver.ReadChoiceAsync(control, token),
            cancellationToken: cancellationToken);
    }

    internal async ValueTask WriteChoiceAsync(
        ClientAuthorization authorization,
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        if (!_driver.Capabilities.Choices.TryGetValue(control, out ChoiceControlDescriptor? descriptor) ||
            _driver is not IRadioChoiceDriver choiceDriver)
        {
            throw new NotSupportedException($"Radio choice '{control}' is not supported by this driver.");
        }

        if (!descriptor.Options.TryGetValue(value, out RadioChoiceOption? option) || !option.Writable)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (option.ApplicableModes is not null && !option.ApplicableModes.Contains(_state.Mode))
        {
            throw new InvalidOperationException($"Choice '{value}' is not applicable in {_state.Mode} mode.");
        }

        await _scheduler.ExecuteAsync(async token =>
        {
            await choiceDriver.WriteChoiceAsync(control, value, token).ConfigureAwait(false);
            RadioChoiceValue confirmed = await choiceDriver.ReadChoiceAsync(control, token).ConfigureAwait(false);
            _events.Publish(RadioEventKind.ControlChanged, confirmed);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<LeaseToken> AcquireLeaseAsync(
        ClientAuthorization authorization,
        string kind,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLeasePermission(authorization, kind);
        LeaseToken lease = _leases.Acquire(kind, authorization.Client, duration);
        _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
        return ValueTask.FromResult(lease);
    }

    internal ValueTask<LeaseToken> RenewLeaseAsync(
        ClientAuthorization authorization,
        LeaseToken lease,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLeasePermission(authorization, lease.Kind);
        LeaseToken renewed = _leases.Renew(lease, authorization.Client, duration);
        _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
        return ValueTask.FromResult(renewed);
    }

    internal async ValueTask ReleaseLeaseAsync(
        ClientAuthorization authorization,
        LeaseToken lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _leases.Release(lease, authorization.Client);
        _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
        if (lease.Kind == LeaseKinds.Transmit && _state.IsTransmitting)
        {
            await ForcePttOffAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask SetPttAsync(
        ClientAuthorization authorization,
        bool enabled,
        LeaseToken transmitLease,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        _leases.Validate(transmitLease, authorization.Client, LeaseKinds.Transmit);
        await _scheduler.ExecuteAsync(async token =>
        {
            _leases.Validate(transmitLease, authorization.Client, LeaseKinds.Transmit);
            await _driver.SetPttAsync(enabled, token).ConfigureAwait(false);
            await RefreshStateCoreAsync(token).ConfigureAwait(false);
        }, enabled ? RadioCommandPriority.Normal : RadioCommandPriority.Safety, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ExecuteExclusiveAsync(
        ClientAuthorization authorization,
        Func<IRadioOperationScope, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        LeaseToken lease = _leases.Acquire(LeaseKinds.ExclusiveControl, authorization.Client, TimeSpan.FromSeconds(30));
        _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
        try
        {
            await _scheduler.ExecuteAsync(async token =>
            {
                var scope = new RadioOperationScope(_driver);
                await operation(scope, token).ConfigureAwait(false);
                await RefreshStateCoreAsync(token).ConfigureAwait(false);
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _leases.Release(lease, authorization.Client);
            _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
        }
    }

    internal async ValueTask CloseSessionAsync(ClientAuthorization authorization)
    {
        IReadOnlyList<LeaseToken> released = _leases.ReleaseAll(authorization.Client);
        if (released.Count == 0)
        {
            return;
        }

        _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
        if (released.Any(lease => lease.Kind == LeaseKinds.Transmit) && _state.IsTransmitting)
        {
            await ForcePttOffAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask ExecuteMutationAsync(
        ClientAuthorization authorization,
        Func<CancellationToken, ValueTask> mutation,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        await _scheduler.ExecuteAsync(async token =>
        {
            await mutation(token).ConfigureAwait(false);
            await RefreshStateCoreAsync(token).ConfigureAwait(false);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RadioState> RefreshStateCoreAsync(CancellationToken cancellationToken)
    {
        RadioState reported = await _driver.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (HasStateChanged(_state, reported))
        {
            _state = reported with { Revision = _state.Revision + 1, ObservedAt = DateTimeOffset.UtcNow };
            _events.Publish(RadioEventKind.StateChanged, _state);
        }
        else
        {
            _state = _state with { ObservedAt = DateTimeOffset.UtcNow };
        }

        return _state;
    }

    private static bool HasStateChanged(RadioState current, RadioState reported) =>
        current.Connection != reported.Connection ||
        current.ActiveVfo != reported.ActiveVfo ||
        current.Mode != reported.Mode ||
        current.IsSplit != reported.IsSplit ||
        current.IsTransmitting != reported.IsTransmitting ||
        current.FrequenciesHz.Count != reported.FrequenciesHz.Count ||
        current.FrequenciesHz.Any(pair =>
            !reported.FrequenciesHz.TryGetValue(pair.Key, out long frequency) || frequency != pair.Value);

    private async ValueTask ForcePttOffAsync(CancellationToken cancellationToken)
    {
        await _scheduler.ExecuteAsync(async token =>
        {
            await _driver.SetPttAsync(false, token).ConfigureAwait(false);
            await RefreshStateCoreAsync(token).ConfigureAwait(false);
        }, RadioCommandPriority.Safety, cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorLeasesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                IReadOnlyList<LeaseToken> expired = _leases.RemoveExpired();
                if (expired.Count == 0)
                {
                    continue;
                }

                _events.Publish(RadioEventKind.LeaseChanged, _leases.Snapshot);
                if (expired.Any(lease => lease.Kind == LeaseKinds.Transmit) && _state.IsTransmitting)
                {
                    await ForcePttOffAsync(_stopping.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private RadioAvailability CreateAvailability(ClientAuthorization authorization) =>
        new(
            Interlocked.Read(ref _availabilityRevision),
            authorization.CanControl ? _driver.Capabilities.Frequency.Targets : new HashSet<VfoId>(),
            authorization.CanControl,
            authorization.CanControl,
            authorization.CanControl && _driver.Capabilities.Transmit.Support == CapabilitySupport.Supported);

    private static ClientAuthorization CreateAuthorization(ClientIdentity client, ClientRole[] roles)
    {
        HashSet<ClientRole> roleSet = roles.Length == 0 ? [ClientRole.Observer] : [.. roles];
        bool canControl = roleSet.Any(role => role >= ClientRole.Operator);
        return new ClientAuthorization(
            1,
            client,
            roleSet,
            true,
            canControl,
            roleSet.Any(role => role >= ClientRole.Controller));
    }

    private static void EnsureCanControl(ClientAuthorization authorization)
    {
        if (!authorization.CanControl)
        {
            throw new UnauthorizedAccessException("The client is not authorized to control this radio.");
        }
    }

    private static void EnsureLeasePermission(ClientAuthorization authorization, string kind)
    {
        EnsureCanControl(authorization);
        if (kind == LeaseKinds.ExclusiveControl && !authorization.CanManageLeases)
        {
            throw new UnauthorizedAccessException("Controller role is required for an exclusive-control lease.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _leaseMonitor.ConfigureAwait(false);
        if (_state.IsTransmitting)
        {
            await ForcePttOffAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _events.Complete();
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _driver.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed class RadioOperationScope(IRadioDriver driver) : IRadioOperationScope
    {
        public ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default) =>
            driver.SetFrequencyAsync(target, frequencyHz, cancellationToken);

        public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
            driver.SetActiveVfoAsync(vfo, cancellationToken);

        public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
            driver.SetModeAsync(mode, cancellationToken);

        public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
            driver.SetSplitAsync(enabled, cancellationToken);

        public ValueTask WriteControlAsync(
            RadioControlId control,
            int value,
            CancellationToken cancellationToken = default) =>
            driver is IRadioControlDriver controlDriver
                ? controlDriver.WriteControlAsync(control, value, cancellationToken)
                : throw new NotSupportedException($"Radio control '{control}' is not supported by this driver.");

        public ValueTask WriteSwitchAsync(
            RadioSwitchId control,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            driver is IRadioSwitchDriver switchDriver
                ? switchDriver.WriteSwitchAsync(control, enabled, cancellationToken)
                : throw new NotSupportedException($"Radio switch '{control}' is not supported by this driver.");

        public ValueTask WriteChoiceAsync(
            RadioChoiceId control,
            string value,
            CancellationToken cancellationToken = default) =>
            driver is IRadioChoiceDriver choiceDriver
                ? choiceDriver.WriteChoiceAsync(control, value, cancellationToken)
                : throw new NotSupportedException($"Radio choice '{control}' is not supported by this driver.");
    }
}
