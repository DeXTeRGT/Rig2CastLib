using System.Threading.Channels;
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
    private IRadioDriver _driver;
    private readonly RadioCommandScheduler _scheduler;
    private readonly RadioLeaseManager _leases;
    private readonly RadioEventHub _events = new();
    private readonly TimeProvider _timeProvider;
    private readonly object _refreshGate = new();
    private readonly Dictionary<VfoId, DateTimeOffset> _frequencyFreshAt = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _leaseMonitor;
    private readonly RadioDriverConnector? _connector;
    private readonly RadioConnectionSupervisorOptions? _connectionOptions;
    private readonly Task _connectionMonitor;
    private readonly Task _commandFailureMonitor;
    private readonly Channel<RadioCommandFailure> _commandFailures = Channel.CreateUnbounded<RadioCommandFailure>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private Task<RadioState>? _sharedStateRefresh;
    private DateTimeOffset _activeVfoFreshAt;
    private DateTimeOffset _modeFreshAt;
    private DateTimeOffset _splitFreshAt;
    private DateTimeOffset _transmitFreshAt;
    private bool _stateCacheValid;
    private RadioState _state;
    private long _connectionGeneration = 1;
    private long _availabilityRevision = 1;
    private int _disposed;

    private ManagedRadio(
        string radioId,
        IRadioDriver driver,
        RadioCommandScheduler scheduler,
        RadioLeaseManager leases,
        RadioState initialState,
        TimeProvider timeProvider,
        RadioDriverConnector? connector = null,
        RadioConnectionSupervisorOptions? connectionOptions = null)
    {
        RadioId = radioId;
        _driver = driver;
        _scheduler = scheduler;
        _leases = leases;
        _state = initialState;
        _timeProvider = timeProvider;
        MarkFullStateFresh(timeProvider.GetUtcNow());
        _connector = connector;
        _connectionOptions = connectionOptions;
        _leaseMonitor = MonitorLeasesAsync();
        _connectionMonitor = MonitorConnectionAsync();
        _commandFailureMonitor = MonitorCommandFailuresAsync();
    }

    public string RadioId { get; }

    public static async ValueTask<ManagedRadio> CreateAsync(
        string radioId,
        IRadioDriver driver,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        var scheduler = new RadioCommandScheduler();
        try
        {
            RadioState state = await scheduler.ExecuteAsync(
                driver.ReadStateAsync,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ManagedRadio(radioId, driver, scheduler, new RadioLeaseManager(clock), state, clock);
        }
        catch
        {
            await scheduler.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<ManagedRadio> CreateReconnectableAsync(
        string radioId,
        RadioDriverConnector connector,
        RadioConnectionSupervisorOptions? connectionOptions = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connector);
        RadioConnectionSupervisorOptions options = connectionOptions ?? new RadioConnectionSupervisorOptions();
        options.Validate();
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        IRadioDriver driver = await connector(cancellationToken).ConfigureAwait(false);
        var scheduler = new RadioCommandScheduler();
        try
        {
            RadioState state = await scheduler.ExecuteAsync(
                driver.ReadStateAsync,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ManagedRadio(
                radioId,
                driver,
                scheduler,
                new RadioLeaseManager(clock),
                state,
                clock,
                connector,
                options);
        }
        catch
        {
            await scheduler.DisposeAsync().ConfigureAwait(false);
            await driver.DisposeAsync().ConfigureAwait(false);
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
        ExecuteHardwareAsync(RefreshStateCoreAsync, cancellationToken: cancellationToken);

    internal async ValueTask<RadioState> ReadStateAsync(
        RadioReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Consistency == RadioReadConsistency.Cached)
            return _state;
        if (request.Consistency == RadioReadConsistency.ForceRefresh)
            return await RefreshStateAsync(cancellationToken).ConfigureAwait(false);

        Task<RadioState> refresh;
        lock (_refreshGate)
        {
            if (IsStateFreshLocked(request.MaximumAge))
                return _state;

            if (_sharedStateRefresh is null)
            {
                _sharedStateRefresh = ExecuteHardwareAsync(
                    RefreshStateCoreAsync,
                    cancellationToken: _stopping.Token).AsTask();
                _ = ClearSharedRefreshAsync(_sharedStateRefresh);
            }
            refresh = _sharedStateRefresh;
        }

        return await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearSharedRefreshAsync(Task<RadioState> refresh)
    {
        try
        {
            await refresh.ConfigureAwait(false);
        }
        catch
        {
            // Every waiter observes the original task result; this continuation only clears it.
        }
        finally
        {
            lock (_refreshGate)
            {
                if (ReferenceEquals(_sharedStateRefresh, refresh))
                    _sharedStateRefresh = null;
            }
        }
    }

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
        CancellationToken cancellationToken) =>
        ExecuteHardwareAsync(
            token => GetControlDriver(control).ReadControlAsync(control, token),
            cancellationToken: cancellationToken);

    internal async ValueTask WriteControlAsync(
        ClientAuthorization authorization,
        RadioControlId control,
        int value,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        await ExecuteHardwareAsync(async token =>
        {
            IRadioControlDriver controlDriver = GetControlDriver(control);
            NumericControlDescriptor descriptor = _driver.Capabilities.Controls[control];
            if (value < descriptor.Minimum || value > descriptor.Maximum ||
                (value - descriptor.Minimum) % descriptor.Step != 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            await controlDriver.WriteControlAsync(control, value, token).ConfigureAwait(false);
            RadioControlValue confirmed = await controlDriver.ReadControlAsync(control, token).ConfigureAwait(false);
            _events.Publish(RadioEventKind.ControlChanged, confirmed);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<RadioMeterReading> ReadMeterAsync(
        RadioMeterId meter,
        CancellationToken cancellationToken) =>
        ExecuteHardwareAsync(
            token => GetMeterDriver(meter).ReadMeterAsync(meter, token),
            cancellationToken: cancellationToken);

    internal ValueTask<RadioSwitchValue> ReadSwitchAsync(
        RadioSwitchId control,
        CancellationToken cancellationToken) =>
        ExecuteHardwareAsync(
            token => GetSwitchDriver(control).ReadSwitchAsync(control, token),
            cancellationToken: cancellationToken);

    internal async ValueTask WriteSwitchAsync(
        ClientAuthorization authorization,
        RadioSwitchId control,
        bool enabled,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        await ExecuteHardwareAsync(async token =>
        {
            IRadioSwitchDriver switchDriver = GetSwitchDriver(control);
            await switchDriver.WriteSwitchAsync(control, enabled, token).ConfigureAwait(false);
            RadioSwitchValue confirmed = await switchDriver.ReadSwitchAsync(control, token).ConfigureAwait(false);
            _events.Publish(RadioEventKind.ControlChanged, confirmed);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<RadioChoiceValue> ReadChoiceAsync(
        RadioChoiceId control,
        CancellationToken cancellationToken) =>
        ExecuteHardwareAsync(
            token => GetChoiceDriver(control).ReadChoiceAsync(control, token),
            cancellationToken: cancellationToken);

    internal async ValueTask WriteChoiceAsync(
        ClientAuthorization authorization,
        RadioChoiceId control,
        string value,
        CancellationToken cancellationToken)
    {
        EnsureCanControl(authorization);
        await ExecuteHardwareAsync(async token =>
        {
            IRadioChoiceDriver choiceDriver = GetChoiceDriver(control);
            ChoiceControlDescriptor descriptor = _driver.Capabilities.Choices[control];
            if (!descriptor.Options.TryGetValue(value, out RadioChoiceOption? option) || !option.Writable)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (option.ApplicableModes is not null && !option.ApplicableModes.Contains(_state.Mode))
                throw new InvalidOperationException($"Choice '{value}' is not applicable in {_state.Mode} mode.");

            await choiceDriver.WriteChoiceAsync(control, value, token).ConfigureAwait(false);
            RadioChoiceValue confirmed = await choiceDriver.ReadChoiceAsync(control, token).ConfigureAwait(false);
            _events.Publish(RadioEventKind.ControlChanged, confirmed);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private IRadioControlDriver GetControlDriver(RadioControlId control) =>
        EnsureConnectedAndGet(_driver.Capabilities.Controls.ContainsKey(control) && _driver is IRadioControlDriver driver
            ? driver
            : throw new NotSupportedException($"Radio control '{control}' is not supported by this driver."));

    private IRadioMeterDriver GetMeterDriver(RadioMeterId meter) =>
        EnsureConnectedAndGet(_driver.Capabilities.Meters.ContainsKey(meter) && _driver is IRadioMeterDriver driver
            ? driver
            : throw new NotSupportedException($"Radio meter '{meter}' is not supported by this driver."));

    private IRadioSwitchDriver GetSwitchDriver(RadioSwitchId control) =>
        EnsureConnectedAndGet(_driver.Capabilities.Switches.ContainsKey(control) && _driver is IRadioSwitchDriver driver
            ? driver
            : throw new NotSupportedException($"Radio switch '{control}' is not supported by this driver."));

    private IRadioChoiceDriver GetChoiceDriver(RadioChoiceId control) =>
        EnsureConnectedAndGet(_driver.Capabilities.Choices.ContainsKey(control) && _driver is IRadioChoiceDriver driver
            ? driver
            : throw new NotSupportedException($"Radio choice '{control}' is not supported by this driver."));

    private T EnsureConnectedAndGet<T>(T value)
    {
        EnsureConnected();
        return value;
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
        await ExecuteHardwareAsync(async token =>
        {
            EnsureConnected();
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
            await ExecuteHardwareAsync(async token =>
            {
                EnsureConnected();
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
        await ExecuteHardwareAsync(async token =>
        {
            EnsureConnected();
            await mutation(token).ConfigureAwait(false);
            await RefreshStateCoreAsync(token).ConfigureAwait(false);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RadioState> RefreshStateCoreAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        RadioState reported = await _driver.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset refreshedAt = _timeProvider.GetUtcNow();
        if (HasStateChanged(_state, reported))
        {
            _state = reported with { Revision = _state.Revision + 1, ObservedAt = refreshedAt };
            _events.Publish(RadioEventKind.StateChanged, _state);
        }
        else
        {
            _state = _state with { ObservedAt = refreshedAt };
        }

        MarkFullStateFresh(refreshedAt);

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

    private void EnsureConnected()
    {
        if (_state.Connection != ConnectionStatus.Connected)
            throw new RadioConnectionUnavailableException(_state.Connection);
    }

    private ValueTask<T> ExecuteHardwareAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        RadioCommandPriority priority = RadioCommandPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        long submittedGeneration = Volatile.Read(ref _connectionGeneration);
        IRadioDriver submittedDriver = _driver;
        return _scheduler.ExecuteAsync(
            async token =>
            {
                EnsureConnectionGeneration(submittedGeneration);
                try
                {
                    return await operation(token).ConfigureAwait(false);
                }
                catch (RadioConnectionException exception)
                {
                    _commandFailures.Writer.TryWrite(new RadioCommandFailure(submittedDriver, exception));
                    throw;
                }
            },
            priority,
            cancellationToken);
    }

    private ValueTask ExecuteHardwareAsync(
        Func<CancellationToken, ValueTask> operation,
        RadioCommandPriority priority = RadioCommandPriority.Normal,
        CancellationToken cancellationToken = default) =>
        ExecuteHardwareAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, priority, cancellationToken).AsVoid();

    private void EnsureConnectionGeneration(long submittedGeneration)
    {
        long currentGeneration = Volatile.Read(ref _connectionGeneration);
        if (submittedGeneration != currentGeneration)
            throw new RadioOperationInvalidatedException(submittedGeneration, currentGeneration);
        EnsureConnected();
    }

    private async ValueTask ForcePttOffAsync(CancellationToken cancellationToken)
    {
        await ExecuteHardwareAsync(async token =>
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

    private async Task MonitorConnectionAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            IRadioDriver observedDriver = _driver;
            if (observedDriver is not IRadioObservationSource source)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
                return;
            }

            Exception? failure = null;
            try
            {
                await foreach (RadioDriverObservation observation in
                    source.WatchObservationsAsync(_stopping.Token).ConfigureAwait(false))
                {
                    await _scheduler.ExecuteAsync(
                        _ =>
                        {
                            ApplyObservation(observation);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken: _stopping.Token).ConfigureAwait(false);
                }

                if (!_stopping.IsCancellationRequested)
                    failure = new IOException("The radio observation stream ended unexpectedly.");
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is null || _stopping.IsCancellationRequested)
                return;

            await RecoverConnectionAsync(observedDriver, failure).ConfigureAwait(false);
        }
    }

    private async Task MonitorCommandFailuresAsync()
    {
        try
        {
            await foreach (RadioCommandFailure failure in
                _commandFailures.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                await RecoverConnectionAsync(failure.Driver, failure.Error).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverConnectionAsync(IRadioDriver failedDriver, Exception failure)
    {
        try
        {
            await _recoveryGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (!ReferenceEquals(_driver, failedDriver) || _stopping.IsCancellationRequested)
                return;

            await MarkConnectionStateAsync(failedDriver, ConnectionStatus.Faulted, failure.Message)
                .ConfigureAwait(false);
            if (_connector is not null && _connectionOptions is not null)
                await ReconnectAsync(failedDriver).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task ReconnectAsync(IRadioDriver failedDriver)
    {
        await MarkConnectionStateAsync(failedDriver, ConnectionStatus.Reconnecting).ConfigureAwait(false);
        TimeSpan delay = _connectionOptions!.InitialRetryDelay;
        int attempt = 0;

        while (!_stopping.IsCancellationRequested && ReferenceEquals(_driver, failedDriver))
        {
            attempt++;
            IRadioDriver? replacement = null;
            try
            {
                replacement = await _connector!(_stopping.Token).ConfigureAwait(false);
                RadioState reported = await replacement.ReadStateAsync(_stopping.Token).ConfigureAwait(false);
                IRadioDriver? replaced = null;
                await _scheduler.ExecuteAsync(
                    _ =>
                    {
                        if (!ReferenceEquals(_driver, failedDriver))
                            return ValueTask.CompletedTask;

                        replaced = _driver;
                        RadioCapabilities previousCapabilities = _driver.Capabilities;
                        Interlocked.Increment(ref _connectionGeneration);
                        _driver = replacement;
                        _state = reported with
                        {
                            Revision = _state.Revision + 1,
                            Connection = ConnectionStatus.Connected,
                            ObservedAt = DateTimeOffset.UtcNow
                        };
                        MarkFullStateFresh(_state.ObservedAt);
                        _events.Publish(RadioEventKind.ConnectionChanged, _state);
                        _events.Publish(RadioEventKind.StateChanged, _state);
                        if (HasCapabilityIdentityChanged(previousCapabilities, replacement.Capabilities))
                            _events.Publish(RadioEventKind.CapabilitiesChanged, replacement.Capabilities);
                        return ValueTask.CompletedTask;
                    },
                    RadioCommandPriority.Safety,
                    _stopping.Token).ConfigureAwait(false);

                if (replaced is null)
                {
                    await replacement.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await replaced.DisposeAsync().ConfigureAwait(false);
                }
                return;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                if (replacement is not null)
                    await replacement.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                if (replacement is not null)
                    await replacement.DisposeAsync().ConfigureAwait(false);
                _events.Publish(
                    RadioEventKind.Diagnostic,
                    new RadioReconnectAttempt(attempt, delay, exception.Message));
            }

            try
            {
                await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }

            double nextMilliseconds = Math.Min(
                _connectionOptions.MaximumRetryDelay.TotalMilliseconds,
                delay.TotalMilliseconds * _connectionOptions.BackoffMultiplier);
            delay = TimeSpan.FromMilliseconds(nextMilliseconds);
        }
    }

    private ValueTask MarkConnectionStateAsync(
        IRadioDriver expectedDriver,
        ConnectionStatus status,
        string? diagnostic = null) =>
        _scheduler.ExecuteAsync(
            _ =>
            {
                if (ReferenceEquals(_driver, expectedDriver) && _state.Connection != status)
                {
                    if (status != ConnectionStatus.Connected)
                    {
                        Interlocked.Increment(ref _connectionGeneration);
                        InvalidateStateFreshness();
                    }
                    _state = _state with
                    {
                        Revision = _state.Revision + 1,
                        Connection = status,
                        ObservedAt = DateTimeOffset.UtcNow
                    };
                    _events.Publish(RadioEventKind.ConnectionChanged, _state);
                    if (diagnostic is not null)
                        _events.Publish(RadioEventKind.Diagnostic, diagnostic);
                }
                return ValueTask.CompletedTask;
            },
            RadioCommandPriority.Safety,
            _stopping.Token);

    private void ApplyObservation(RadioDriverObservation observation)
    {
        if (observation.Kind == RadioDriverObservationKind.Ignored)
        {
            return;
        }

        MarkObservationFresh(observation, _timeProvider.GetUtcNow());

        RadioState updated = observation.Kind switch
        {
            RadioDriverObservationKind.FrequencyChanged
                when observation.Vfo is VfoId vfo && observation.FrequencyHz is long frequency =>
                _state with
                {
                    FrequenciesHz = new Dictionary<VfoId, long>(_state.FrequenciesHz) { [vfo] = frequency }
                },
            RadioDriverObservationKind.ActiveVfoChanged when observation.Vfo is VfoId vfo =>
                _state with { ActiveVfo = vfo },
            RadioDriverObservationKind.ModeChanged when observation.Mode is RadioMode mode =>
                _state with { Mode = mode },
            RadioDriverObservationKind.SplitChanged when observation.Flag is bool split =>
                _state with { IsSplit = split },
            RadioDriverObservationKind.TransmitChanged when observation.Flag is bool transmitting =>
                _state with { IsTransmitting = transmitting },
            RadioDriverObservationKind.StateInformation
                when observation.Vfo is VfoId vfo &&
                     observation.FrequencyHz is long frequency &&
                     observation.Mode is RadioMode mode =>
                _state with
                {
                    FrequenciesHz = new Dictionary<VfoId, long>(_state.FrequenciesHz) { [vfo] = frequency },
                    Mode = mode
                },
            _ => _state
        };

        if (observation.Kind != RadioDriverObservationKind.Unknown && HasStateChanged(_state, updated))
        {
            _state = updated with
            {
                Revision = _state.Revision + 1,
                ObservedAt = observation.ObservedAt
            };
            _events.Publish(RadioEventKind.StateChanged, _state);
        }
        else if (observation.Kind == RadioDriverObservationKind.Unknown)
        {
            _events.Publish(RadioEventKind.Diagnostic, observation);
        }
    }

    private static bool HasCapabilityIdentityChanged(
        RadioCapabilities previous,
        RadioCapabilities current) =>
        previous.Revision != current.Revision ||
        !StringComparer.Ordinal.Equals(previous.Manufacturer, current.Manufacturer) ||
        !StringComparer.Ordinal.Equals(previous.Model, current.Model) ||
        !StringComparer.Ordinal.Equals(previous.DriverId, current.DriverId) ||
        !StringComparer.Ordinal.Equals(previous.DriverVersion, current.DriverVersion);

    private bool IsStateFreshLocked(TimeSpan maximumAge)
    {
        if (!_stateCacheValid || _state.Connection != ConnectionStatus.Connected)
            return false;

        DateTimeOffset threshold = _timeProvider.GetUtcNow() - maximumAge;
        return _state.FrequenciesHz.Keys.All(vfo =>
                   _frequencyFreshAt.TryGetValue(vfo, out DateTimeOffset observed) && observed >= threshold) &&
               _activeVfoFreshAt >= threshold &&
               _modeFreshAt >= threshold &&
               _splitFreshAt >= threshold &&
               _transmitFreshAt >= threshold;
    }

    private void MarkFullStateFresh(DateTimeOffset observedAt)
    {
        lock (_refreshGate)
        {
            foreach (VfoId vfo in _state.FrequenciesHz.Keys)
                _frequencyFreshAt[vfo] = observedAt;
            _activeVfoFreshAt = observedAt;
            _modeFreshAt = observedAt;
            _splitFreshAt = observedAt;
            _transmitFreshAt = observedAt;
            _stateCacheValid = true;
        }
    }

    private void MarkObservationFresh(RadioDriverObservation observation, DateTimeOffset observedAt)
    {
        lock (_refreshGate)
        {
            switch (observation.Kind)
            {
                case RadioDriverObservationKind.FrequencyChanged when observation.Vfo is VfoId vfo:
                    _frequencyFreshAt[vfo] = observedAt;
                    break;
                case RadioDriverObservationKind.ActiveVfoChanged:
                    _activeVfoFreshAt = observedAt;
                    break;
                case RadioDriverObservationKind.ModeChanged:
                    _modeFreshAt = observedAt;
                    break;
                case RadioDriverObservationKind.SplitChanged:
                    _splitFreshAt = observedAt;
                    break;
                case RadioDriverObservationKind.TransmitChanged:
                    _transmitFreshAt = observedAt;
                    break;
                case RadioDriverObservationKind.StateInformation when observation.Vfo is VfoId vfo:
                    _frequencyFreshAt[vfo] = observedAt;
                    _modeFreshAt = observedAt;
                    break;
            }
        }
    }

    private void InvalidateStateFreshness()
    {
        lock (_refreshGate)
        {
            _stateCacheValid = false;
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
        await _connectionMonitor.ConfigureAwait(false);
        await _commandFailureMonitor.ConfigureAwait(false);
        if (_state.IsTransmitting)
        {
            await ForcePttOffAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _events.Complete();
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _driver.DisposeAsync().ConfigureAwait(false);
        _recoveryGate.Dispose();
        _stopping.Dispose();
    }

    private sealed record RadioCommandFailure(IRadioDriver Driver, RadioConnectionException Error);

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
