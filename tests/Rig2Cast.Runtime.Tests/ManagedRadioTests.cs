using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Runtime.Leases;

namespace Rig2Cast.Runtime.Tests;

public sealed class ManagedRadioTests
{
    [Fact]
    public async Task CachedAndFreshStateReadsAvoidUnnecessaryDriverReads()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using TestContext context = await TestContext.CreateAsync(timeProvider: clock);
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("reader"));
        int initialReads = context.Driver.CommandLog.Count(command => command == "ReadState");

        _ = await session.ReadStateAsync(RadioReadRequest.Cached);
        _ = await session.ReadStateAsync(RadioReadRequest.FreshWithin(TimeSpan.FromSeconds(1)));

        Assert.Equal(initialReads, context.Driver.CommandLog.Count(command => command == "ReadState"));
    }

    [Fact]
    public async Task ExpiredFreshStateReadQueriesDriver()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using TestContext context = await TestContext.CreateAsync(timeProvider: clock);
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("reader"));
        int initialReads = context.Driver.CommandLog.Count(command => command == "ReadState");
        clock.Advance(TimeSpan.FromSeconds(2));

        _ = await session.ReadStateAsync(RadioReadRequest.FreshWithin(TimeSpan.FromSeconds(1)));

        Assert.Equal(initialReads + 1, context.Driver.CommandLog.Count(command => command == "ReadState"));
    }

    [Fact]
    public async Task ConcurrentFreshStateReadsAreCoalesced()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using TestContext context = await TestContext.CreateAsync(
            TimeSpan.FromMilliseconds(50), clock);
        await using IRadioSession first = context.Radio.OpenSession(new ClientIdentity("first"));
        await using IRadioSession second = context.Radio.OpenSession(new ClientIdentity("second"));
        int initialReads = context.Driver.CommandLog.Count(command => command == "ReadState");
        clock.Advance(TimeSpan.FromSeconds(2));
        RadioReadRequest request = RadioReadRequest.FreshWithin(TimeSpan.FromSeconds(1));

        Task<RadioState>[] reads = Enumerable.Range(0, 10)
            .Select(index => (index % 2 == 0 ? first : second).ReadStateAsync(request).AsTask())
            .ToArray();
        RadioState[] states = await Task.WhenAll(reads);

        Assert.Equal(initialReads + 1, context.Driver.CommandLog.Count(command => command == "ReadState"));
        Assert.All(states, state => Assert.Same(states[0], state));
    }

    [Fact]
    public async Task CancellingOneFreshWaiterDoesNotCancelSharedRefresh()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using TestContext context = await TestContext.CreateAsync(
            TimeSpan.FromMilliseconds(100), clock);
        await using IRadioSession first = context.Radio.OpenSession(new ClientIdentity("first"));
        await using IRadioSession second = context.Radio.OpenSession(new ClientIdentity("second"));
        clock.Advance(TimeSpan.FromSeconds(2));
        RadioReadRequest request = RadioReadRequest.FreshWithin(TimeSpan.FromSeconds(1));
        using var firstStopping = new CancellationTokenSource();

        Task<RadioState> cancelled = first.ReadStateAsync(request, firstStopping.Token).AsTask();
        Task<RadioState> surviving = second.ReadStateAsync(request).AsTask();
        firstStopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(ConnectionStatus.Connected, (await surviving).Connection);
    }

    [Fact]
    public async Task SnapshotReportsVfoBCapability()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("gui"), ClientRole.Observer);
        RadioSnapshot snapshot = await session.GetSnapshotAsync();
        Assert.Contains(VfoId.B, snapshot.Capabilities.Vfos.Available);
        Assert.Contains(VfoId.B, snapshot.Capabilities.Frequency.Targets);
        Assert.False(snapshot.Authorization.CanControl);
        Assert.Empty(snapshot.Availability.WritableVfos);
    }

    [Fact]
    public async Task ConcurrentClientsAreSerializedAtTheDriver()
    {
        await using TestContext context = await TestContext.CreateAsync(TimeSpan.FromMilliseconds(20));
        await using IRadioSession first = context.Radio.OpenSession(new ClientIdentity("first"), ClientRole.Operator);
        await using IRadioSession second = context.Radio.OpenSession(new ClientIdentity("second"), ClientRole.Operator);
        Task[] operations = Enumerable.Range(0, 12)
            .Select(index => (index % 2 == 0 ? first : second)
                .SetFrequencyAsync(VfoId.A, 14_200_000 + index).AsTask())
            .ToArray();
        await Task.WhenAll(operations);
        Assert.Equal(1, context.Driver.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task ActiveVfoSelectionIsAuthorizedAndReflectedInState()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession observer = context.Radio.OpenSession(new ClientIdentity("observer"), ClientRole.Observer);
        await using IRadioSession operatorSession = context.Radio.OpenSession(new ClientIdentity("operator"), ClientRole.Operator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.SetActiveVfoAsync(VfoId.B).AsTask());
        await operatorSession.SetActiveVfoAsync(VfoId.B);

        Assert.Equal(VfoId.B, (await operatorSession.GetSnapshotAsync()).State.ActiveVfo);
        Assert.Equal(
            [new RadioSignalPath(ReceiverId.Main, VfoId.B)],
            (await operatorSession.GetSnapshotAsync()).State.ReceivePaths);
        Assert.Contains("SetActiveVfo:B", context.Driver.CommandLog);
    }

    [Fact]
    public async Task LegacyMutationsRejectCapabilityViolationsBeforeCallingDriver()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);
        int initialCommands = context.Driver.CommandLog.Count;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetFrequencyAsync(VfoId.Memory, 14_200_000).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.SetFrequencyAsync(VfoId.A, 1).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetActiveVfoAsync(VfoId.Memory).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetModeAsync(RadioMode.Psk).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetSplitAsync(true, VfoId.Memory).AsTask());

        Assert.Equal(initialCommands, context.Driver.CommandLog.Count);
    }

    [Fact]
    public async Task SimulatorAdvertisesEveryTypedControlCategory()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("audit"), ClientRole.Observer);
        RadioCapabilities capabilities = (await session.GetSnapshotAsync()).Capabilities;

        Assert.Equal(Enum.GetValues<RadioControlId>().Order(), capabilities.Controls.Keys.Order());
        Assert.Equal(Enum.GetValues<RadioSwitchId>().Order(), capabilities.Switches.Keys.Order());
        Assert.Equal(Enum.GetValues<RadioChoiceId>().Order(), capabilities.Choices.Keys.Order());
        Assert.Equal(Enum.GetValues<RadioMeterId>().Order(), capabilities.Meters.Keys.Order());
    }

    [Fact]
    public async Task ObserverCannotMutateRadio()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession observer = context.Radio.OpenSession(new ClientIdentity("observer"), ClientRole.Observer);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => observer.SetModeAsync(RadioMode.Cw).AsTask());
    }

    [Fact]
    public async Task PttRequiresCurrentTransmitLeaseOwnedByClient()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession first = context.Radio.OpenSession(new ClientIdentity("first"), ClientRole.Operator);
        await using IRadioSession second = context.Radio.OpenSession(new ClientIdentity("second"), ClientRole.Operator);
        LeaseToken lease = await first.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidLeaseException>(() => second.SetPttAsync(true, lease).AsTask());
        await first.SetPttAsync(true, lease);
        Assert.True((await first.GetSnapshotAsync()).State.IsTransmitting);
    }

    [Fact]
    public async Task UnsupportedPttIsRejectedBeforeCallingDriver()
    {
        var driver = new UnsupportedTransmitDriver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("unsupported-transmit", driver);
        await using IRadioSession session = radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);
        LeaseToken lease = await session.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(5));

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetPttAsync(true, lease).AsTask());

        Assert.Equal("Transmit is not writable on this radio.", exception.Message);
        Assert.Equal(0, driver.SetPttCallCount);
    }

    [Fact]
    public async Task CompetingTransmitLeaseIsRejected()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession first = context.Radio.OpenSession(new ClientIdentity("first"), ClientRole.Operator);
        await using IRadioSession second = context.Radio.OpenSession(new ClientIdentity("second"), ClientRole.Operator);
        _ = await first.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<LeaseUnavailableException>(
            () => second.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(5)).AsTask());
    }

    [Fact]
    public void SameOwnerMustRenewRatherThanSilentlySupersedeActiveLease()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var manager = new RadioLeaseManager(clock);
        var owner = new ClientIdentity("owner");
        LeaseToken original = manager.Acquire(LeaseKinds.Transmit, owner, TimeSpan.FromSeconds(5));

        Assert.Throws<LeaseUnavailableException>(
            () => manager.Acquire(LeaseKinds.Transmit, owner, TimeSpan.FromSeconds(10)));
        manager.Validate(original, owner, LeaseKinds.Transmit);
    }

    [Fact]
    public void ExpiredLeaseCanBeReplacedAtomicallyWithoutBackgroundCleanup()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var manager = new RadioLeaseManager(clock);
        var first = new ClientIdentity("first");
        var second = new ClientIdentity("second");
        LeaseToken expired = manager.Acquire(LeaseKinds.Transmit, first, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));

        LeaseToken replacement = manager.Acquire(LeaseKinds.Transmit, second, TimeSpan.FromSeconds(5));

        manager.Validate(replacement, second, LeaseKinds.Transmit);
        Assert.Throws<InvalidLeaseException>(
            () => manager.Validate(expired, first, LeaseKinds.Transmit));
    }

    [Fact]
    public async Task DisposingLeaseOwnerForcesPttOff()
    {
        await using TestContext context = await TestContext.CreateAsync();
        IRadioSession transmitter = context.Radio.OpenSession(new ClientIdentity("tx"), ClientRole.Operator);
        await using IRadioSession observer = context.Radio.OpenSession(new ClientIdentity("observer"), ClientRole.Observer);
        LeaseToken lease = await transmitter.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(5));
        await transmitter.SetPttAsync(true, lease);
        await transmitter.DisposeAsync();
        Assert.False((await observer.GetSnapshotAsync()).State.IsTransmitting);
        Assert.Contains("SetPtt:False", context.Driver.CommandLog);
    }

    [Fact]
    public async Task ExpiredTransmitLeaseForcesPttOff()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession transmitter = context.Radio.OpenSession(new ClientIdentity("tx"), ClientRole.Operator);
        LeaseToken lease = await transmitter.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromMilliseconds(80));
        await transmitter.SetPttAsync(true, lease);
        await WaitUntilAsync(async () => !(await transmitter.GetSnapshotAsync()).State.IsTransmitting, TimeSpan.FromSeconds(2));
        Assert.Empty((await transmitter.GetSnapshotAsync()).Leases.Active);
    }

    [Fact]
    public async Task RenewingTransmitControllerKeepsLeaseAliveUntilExplicitStop()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession transmitter = context.Radio.OpenSession(
            new ClientIdentity("tx"), ClientRole.Operator);
        await using var controller = new RenewingTransmitController(
            transmitter,
            leaseDuration: TimeSpan.FromMilliseconds(180),
            renewalInterval: TimeSpan.FromMilliseconds(40));

        RadioState keyed = await controller.StartContinuousAsync();
        Assert.True(keyed.IsTransmitting);
        await Task.Delay(450);
        Assert.True((await controller.GetStatusAsync()).IsTransmitting);
        Assert.Single((await transmitter.GetSnapshotAsync()).Leases.Active);

        RadioState released = await controller.StopAsync();
        Assert.False(released.IsTransmitting);
        Assert.Empty((await transmitter.GetSnapshotAsync()).Leases.Active);
        Assert.Contains("SetPtt:False", context.Driver.CommandLog);
    }

    [Fact]
    public async Task RenewingTransmitControllerBoundedStartExpiresAndForcesReceive()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession transmitter = context.Radio.OpenSession(
            new ClientIdentity("tx"), ClientRole.Operator);
        await using var controller = new RenewingTransmitController(
            transmitter,
            leaseDuration: TimeSpan.FromMilliseconds(180),
            renewalInterval: TimeSpan.FromMilliseconds(40));

        Assert.True((await controller.StartForAsync(TimeSpan.FromMilliseconds(100))).IsTransmitting);
        await WaitUntilAsync(
            async () => !(await controller.GetStatusAsync()).IsTransmitting,
            TimeSpan.FromSeconds(2));

        Assert.Empty((await transmitter.GetSnapshotAsync()).Leases.Active);
        Assert.Contains("SetPtt:False", context.Driver.CommandLog);
    }

    [Fact]
    public async Task RenewingTransmitControllerUsesConfiguredTimeProviderForExpiryDecision()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using TestContext context = await TestContext.CreateAsync(timeProvider: clock);
        await using IRadioSession transmitter = context.Radio.OpenSession(
            new ClientIdentity("tx"), ClientRole.Operator);
        await using var controller = new RenewingTransmitController(
            transmitter,
            leaseDuration: TimeSpan.FromSeconds(10),
            renewalInterval: TimeSpan.FromSeconds(5),
            timeProvider: clock);

        Assert.True((await controller.StartForAsync(TimeSpan.FromSeconds(5))).IsTransmitting);
        clock.Advance(TimeSpan.FromSeconds(6));

        RadioState stopped = await controller.StopAsync();

        Assert.False(stopped.IsTransmitting);
        Assert.Empty((await transmitter.GetSnapshotAsync()).Leases.Active);
    }

    [Fact]
    public async Task LeaseMonitorRetriesFailedDekeyAndRemainsAvailableForLaterExpiry()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession transmitter = context.Radio.OpenSession(
            new ClientIdentity("tx"), ClientRole.Operator);

        LeaseToken first = await transmitter.AcquireLeaseAsync(
            LeaseKinds.Transmit, TimeSpan.FromMilliseconds(120));
        await transmitter.SetPttAsync(true, first);
        context.Driver.FailNextCommand(new InvalidOperationException("Simulated de-key failure."));

        await WaitUntilAsync(
            async () => !(await transmitter.GetSnapshotAsync()).State.IsTransmitting,
            TimeSpan.FromSeconds(2));
        Assert.True(context.Driver.CommandLog.Count(command => command == "SetPtt:False") >= 2);

        LeaseToken second = await transmitter.AcquireLeaseAsync(
            LeaseKinds.Transmit, TimeSpan.FromMilliseconds(120));
        await transmitter.SetPttAsync(true, second);
        await WaitUntilAsync(
            async () => !(await transmitter.GetSnapshotAsync()).State.IsTransmitting,
            TimeSpan.FromSeconds(2));

        Assert.Empty((await transmitter.GetSnapshotAsync()).Leases.Active);
        Assert.True(context.Driver.CommandLog.Count(command => command == "SetPtt:False") >= 3);
    }

    [Fact]
    public async Task RenewingTransmitControllerLosesSessionAndRadioReturnsToReceive()
    {
        await using TestContext context = await TestContext.CreateAsync();
        IRadioSession transmitter = context.Radio.OpenSession(new ClientIdentity("tx"), ClientRole.Operator);
        await using IRadioSession observer = context.Radio.OpenSession(
            new ClientIdentity("observer"), ClientRole.Observer);
        await using var controller = new RenewingTransmitController(
            transmitter,
            leaseDuration: TimeSpan.FromMilliseconds(180),
            renewalInterval: TimeSpan.FromMilliseconds(40));

        Assert.True((await controller.StartContinuousAsync()).IsTransmitting);
        await transmitter.DisposeAsync();

        await WaitUntilAsync(
            async () => !(await observer.GetSnapshotAsync()).State.IsTransmitting,
            TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => Task.FromResult(controller.RenewalFailure is not null),
            TimeSpan.FromSeconds(2));
        Assert.Empty((await observer.GetSnapshotAsync()).Leases.Active);
    }

    [Fact]
    public async Task PttVerificationWaitsForDelayedHardwareReadback()
    {
        await using TestContext context = await TestContext.CreateAsync(pttReadbackLagCount: 3);
        await using IRadioSession transmitter = context.Radio.OpenSession(
            new ClientIdentity("tx"), ClientRole.Operator);
        LeaseToken lease = await transmitter.AcquireLeaseAsync(
            LeaseKinds.Transmit, TimeSpan.FromSeconds(5));

        await transmitter.SetPttAsync(true, lease);
        int readsBeforeRelease = context.Driver.ReadStateCount;
        await transmitter.SetPttAsync(false, lease);

        Assert.False((await transmitter.GetSnapshotAsync()).State.IsTransmitting);
        Assert.True(context.Driver.ReadStateCount - readsBeforeRelease >= 4);
        await transmitter.ReleaseLeaseAsync(lease);
    }

    [Fact]
    public async Task ReceiverTargetedFrequencyAndModeUseCommonRuntimePath()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);

        await session.SetFrequencyAsync(ReceiverId.Main, 14_275_000);
        await session.SetModeAsync(ReceiverId.Main, RadioMode.Cw);
        RadioState state = await session.RefreshStateAsync();

        Assert.Equal(14_275_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(RadioMode.Cw, state.Receivers[ReceiverId.Main].Mode);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetFrequencyAsync(ReceiverId.Sub, 7_100_000).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetModeAsync(ReceiverId.Sub, RadioMode.Usb).AsTask());
    }

    [Fact]
    public async Task ExclusiveScopeDoesNotAllowInterleavedMutation()
    {
        await using TestContext context = await TestContext.CreateAsync(TimeSpan.FromMilliseconds(25));
        await using IRadioSession controller = context.Radio.OpenSession(new ClientIdentity("controller"), ClientRole.Controller);
        await using IRadioSession other = context.Radio.OpenSession(new ClientIdentity("other"), ClientRole.Operator);
        Task exclusive = controller.ExecuteExclusiveAsync(async (scope, cancellationToken) =>
        {
            await scope.SetFrequencyAsync(VfoId.A, 7_050_000, cancellationToken);
            await scope.SetModeAsync(RadioMode.Cw, cancellationToken);
        }).AsTask();
        await Task.Delay(5);
        Task competing = other.SetSplitAsync(true).AsTask();
        await Task.WhenAll(exclusive, competing);
        string[] writes = context.Driver.CommandLog
            .Where(command => command.StartsWith("Set", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["SetFrequency:A:7050000", "SetMode:Cw", "SetSplit:True"], writes);
    }

    [Fact]
    public async Task MutationPublishesVersionedStateEvent()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("gui"), ClientRole.Operator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task<RadioEvent> received = ReadFirstEventAsync(session, timeout.Token);
        await Task.Delay(10, timeout.Token);
        await session.SetModeAsync(RadioMode.Cw, timeout.Token);
        RadioEvent radioEvent = await received;
        Assert.Equal(RadioEventKind.StateChanged, radioEvent.Kind);
        RadioState state = Assert.IsType<RadioState>(radioEvent.Payload);
        Assert.Equal(RadioMode.Cw, state.Mode);
        Assert.True(state.Revision > 1);
    }

    [Fact]
    public async Task SlowEventSubscriberIsBoundedAndReceivesDeliveryGapDiagnostic()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(
            new ClientIdentity("slow-subscriber"), ClientRole.Operator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<RadioEvent> events = session
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        Task<bool> subscription = events.MoveNextAsync().AsTask();
        await session.SetFrequencyAsync(VfoId.A, 14_200_001);
        Assert.True(await subscription);

        for (int index = 0; index < 300; index++)
            await session.SetFrequencyAsync(VfoId.A, 14_201_000 + index);

        Assert.True(await events.MoveNextAsync());
        RadioEvent diagnostic = events.Current;
        RadioEventDeliveryGap gap = Assert.IsType<RadioEventDeliveryGap>(diagnostic.Payload);
        Assert.Equal(RadioEventKind.Diagnostic, diagnostic.Kind);
        Assert.Equal(44, gap.DroppedCount);
        Assert.Equal(256, gap.SubscriberCapacity);
        Assert.True(gap.FirstDroppedSequence <= gap.LastDroppedSequence);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(RadioEventKind.StateChanged, events.Current.Kind);
        Assert.True(events.Current.Sequence > gap.LastDroppedSequence);
    }

    [Fact]
    public async Task LiveRefreshObservesExternalRadioChangesAndUpdatesCache()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("console"), ClientRole.Observer);
        RadioSnapshot before = await session.GetSnapshotAsync();

        await context.Driver.SetFrequencyAsync(VfoId.A, 14_256_789);
        Assert.Equal(before.State.FrequenciesHz[VfoId.A], (await session.GetSnapshotAsync()).State.FrequenciesHz[VfoId.A]);

        RadioState refreshed = await session.RefreshStateAsync();

        Assert.Equal(14_256_789, refreshed.FrequenciesHz[VfoId.A]);
        Assert.True(refreshed.Revision > before.State.Revision);
        Assert.Equal(14_256_789, (await session.GetSnapshotAsync()).State.FrequenciesHz[VfoId.A]);
    }

    [Fact]
    public async Task UnsolicitedSimulatorChangeUpdatesCacheWithoutPolling()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("gui"), ClientRole.Observer);

        await context.Driver.SimulateFrequencyChangeAsync(VfoId.A, 14_275_000);
        await WaitUntilAsync(
            async () => (await session.GetSnapshotAsync()).State.FrequenciesHz[VfoId.A] == 14_275_000,
            TimeSpan.FromSeconds(2));

        Assert.Equal(14_275_000, (await session.GetSnapshotAsync()).State.FrequenciesHz[VfoId.A]);
    }

    [Fact]
    public async Task DuplicateRecognizedObservationDoesNotPublishDiagnosticEvent()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("observer"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using IAsyncEnumerator<RadioEvent> events = session
            .WatchEventsAsync(timeout.Token)
            .GetAsyncEnumerator();
        Task<bool> nextEvent = events.MoveNextAsync().AsTask();

        await context.Driver.SimulateFrequencyChangeAsync(VfoId.A, 14_200_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextEvent);
    }

    [Fact]
    public async Task ConnectionSupervisorReplacesFaultedDriverAndRefreshesState()
    {
        var drivers = new List<SimulatedFtdx10Driver>();
        async ValueTask<IRadioDriver> ConnectAsync(CancellationToken cancellationToken)
        {
            var driver = new SimulatedFtdx10Driver();
            if (drivers.Count > 0)
                await driver.SetFrequencyAsync(VfoId.A, 14_300_000, cancellationToken);
            drivers.Add(driver);
            return driver;
        }

        await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
            "reconnecting-radio",
            ConnectAsync,
            new RadioConnectionSupervisorOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
            });
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("gui"));
        long initialRevision = (await session.GetSnapshotAsync()).State.Revision;

        drivers[0].SimulateConnectionFailure();

        await WaitUntilAsync(async () =>
        {
            RadioState state = (await session.GetSnapshotAsync()).State;
            return drivers.Count >= 2 &&
                   state.Connection == ConnectionStatus.Connected &&
                   state.FrequenciesHz[VfoId.A] == 14_300_000;
        }, TimeSpan.FromSeconds(2));

        RadioState recovered = (await session.GetSnapshotAsync()).State;
        Assert.True(recovered.Revision > initialRevision);
        Assert.Equal(2, drivers.Count);
    }

    [Fact]
    public async Task ReconnectForcesReceiveBeforePublishingUnleasedTransmittingReplacement()
    {
        var first = new SimulatedFtdx10Driver();
        var replacement = new SimulatedFtdx10Driver();
        await replacement.SetPttAsync(true);
        int connections = 0;
        ValueTask<IRadioDriver> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IRadioDriver>(
                Interlocked.Increment(ref connections) == 1 ? first : replacement);

        await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
            "safe-reconnect-radio",
            ConnectAsync,
            new RadioConnectionSupervisorOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
            });
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("observer"));

        first.SimulateConnectionFailure();
        await WaitUntilAsync(async () =>
            connections == 2 &&
            (await session.GetSnapshotAsync()).State.Connection == ConnectionStatus.Connected,
            TimeSpan.FromSeconds(2));

        RadioState state = await session.RefreshStateAsync();
        Assert.False(state.IsTransmitting);
        Assert.Contains("SetPtt:False", replacement.CommandLog);
    }

    [Fact]
    public async Task DisposalClosesDriverWhenDekeyAndDriverDisposalFail()
    {
        var driver = new SimulatedFtdx10Driver(new SimulatedRadioOptions
        {
            DisposeException = new IOException("Simulated transport close failure.")
        });
        ManagedRadio radio = await ManagedRadio.CreateAsync("disposal-radio", driver);
        IRadioSession session = radio.OpenSession(new ClientIdentity("tx"), ClientRole.Operator);
        LeaseToken lease = await session.AcquireLeaseAsync(LeaseKinds.Transmit, TimeSpan.FromSeconds(5));
        await session.SetPttAsync(true, lease);
        driver.FailNextCommand(new InvalidOperationException("Simulated shutdown de-key failure."));

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => radio.DisposeAsync().AsTask());

        Assert.True(driver.IsDisposed);
        Assert.Contains(failure.InnerExceptions, exception =>
            exception.Message.Contains("de-key", StringComparison.Ordinal));
        Assert.Contains(failure.InnerExceptions, exception =>
            exception.Message.Contains("transport close", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentManagedRadioDisposalWaitsForSingleCleanupCompletion()
    {
        var driver = new SimulatedFtdx10Driver(new SimulatedRadioOptions
        {
            CommandDelay = TimeSpan.FromSeconds(5)
        });
        ManagedRadio radio = await ManagedRadio.CreateAsync("concurrent-disposal", driver);
        IRadioSession session = radio.OpenSession(new ClientIdentity("reader"), ClientRole.Operator);
        Task<RadioMeterReading> operation = session.ReadMeterAsync(RadioMeterId.SignalStrength).AsTask();
        await WaitUntilAsync(
            () => Task.FromResult(driver.CommandLog.Contains("ReadMeter:SignalStrength")),
            TimeSpan.FromSeconds(1));
        Task queued = session.SetFrequencyAsync(VfoId.A, 14_300_000).AsTask();

        Task[] disposals = Enumerable.Range(0, 8)
            .Select(_ => radio.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.DoesNotContain("SetFrequency:A:14300000", driver.CommandLog);
        Assert.True(driver.IsDisposed);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ManagedRadioDisposalReleasesCancellationInsensitiveObservationRead()
    {
        var driver = new CancellationInsensitiveObservationDriver();
        ManagedRadio radio = await ManagedRadio.CreateAsync("insensitive-observation", driver);

        await radio.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(driver.IsDisposed);
    }

    [Fact]
    public async Task RuntimeEventsUseConfiguredTimeProvider()
    {
        DateTimeOffset expected = new(2035, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var clock = new ManualTimeProvider(expected);
        await using TestContext context = await TestContext.CreateAsync(timeProvider: clock);
        await using IRadioSession session = context.Radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task<RadioEvent> nextEvent = ReadFirstEventAsync(session, timeout.Token);

        await session.SetModeAsync(RadioMode.Cw);
        RadioEvent radioEvent = await nextEvent;

        Assert.Equal(expected, radioEvent.OccurredAt);
    }

    [Fact]
    public async Task QueuedSetterIsInvalidatedInsteadOfReplayedAfterReconnect()
    {
        var first = new SimulatedFtdx10Driver(new SimulatedRadioOptions
        {
            CommandDelay = TimeSpan.FromMilliseconds(250)
        });
        var replacement = new SimulatedFtdx10Driver();
        int connections = 0;
        ValueTask<IRadioDriver> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IRadioDriver>(
                Interlocked.Increment(ref connections) == 1 ? first : replacement);

        await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
            "generation-radio",
            ConnectAsync,
            new RadioConnectionSupervisorOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
            });
        await using IRadioSession session = radio.OpenSession(
            new ClientIdentity("controller"), ClientRole.Controller);

        Task<RadioMeterReading> blockingRead = session
            .ReadMeterAsync(RadioMeterId.SignalStrength)
            .AsTask();
        await WaitUntilAsync(
            () => Task.FromResult(first.CommandLog.Contains("ReadMeter:SignalStrength")),
            TimeSpan.FromSeconds(1));
        Task setter = session.SetFrequencyAsync(VfoId.A, 14_350_000).AsTask();
        first.SimulateConnectionFailure();

        _ = await blockingRead;
        RadioOperationInvalidatedException invalidated =
            await Assert.ThrowsAsync<RadioOperationInvalidatedException>(() => setter);
        await WaitUntilAsync(async () =>
            (await session.GetSnapshotAsync()).State.Connection == ConnectionStatus.Connected &&
            connections == 2,
            TimeSpan.FromSeconds(2));

        Assert.True(invalidated.CurrentGeneration > invalidated.SubmittedGeneration);
        Assert.DoesNotContain("SetFrequency:A:14350000", first.CommandLog);
        Assert.DoesNotContain("SetFrequency:A:14350000", replacement.CommandLog);
        Assert.Equal(14_200_000, (await session.GetSnapshotAsync()).State.FrequenciesHz[VfoId.A]);
    }

    [Fact]
    public async Task CommandPathConnectionFailureTriggersSupervisedReconnect()
    {
        var first = new SimulatedFtdx10Driver();
        var replacement = new SimulatedFtdx10Driver();
        int connections = 0;
        ValueTask<IRadioDriver> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IRadioDriver>(
                Interlocked.Increment(ref connections) == 1 ? first : replacement);

        await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
            "command-failure-radio",
            ConnectAsync,
            new RadioConnectionSupervisorOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
            });
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("reader"));
        first.FailNextCommand(new RadioConnectionException("Simulated transport failure."));

        await Assert.ThrowsAsync<RadioConnectionException>(
            () => session.ReadMeterAsync(RadioMeterId.SignalStrength).AsTask());
        await WaitUntilAsync(async () =>
            connections == 2 &&
            (await session.GetSnapshotAsync()).State.Connection == ConnectionStatus.Connected,
            TimeSpan.FromSeconds(2));

        Assert.Equal(
            RadioMeterId.SignalStrength,
            (await session.ReadMeterAsync(RadioMeterId.SignalStrength)).Id);
    }

    [Fact]
    public async Task OrdinaryCommandErrorDoesNotTriggerReconnect()
    {
        var driver = new SimulatedFtdx10Driver();
        int connections = 0;
        ValueTask<IRadioDriver> ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref connections);
            return ValueTask.FromResult<IRadioDriver>(driver);
        }

        await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
            "command-error-radio",
            ConnectAsync,
            new RadioConnectionSupervisorOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
            });
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("reader"));
        driver.FailNextCommand(new InvalidOperationException("Radio rejected the command."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ReadMeterAsync(RadioMeterId.SignalStrength).AsTask());
        await Task.Delay(100);

        Assert.Equal(1, connections);
        Assert.Equal(ConnectionStatus.Connected, (await session.GetSnapshotAsync()).State.Connection);
    }

    [Fact]
    public async Task ConnectionSupervisorRetriesFailedReconnectWithBackoff()
    {
        var first = new SimulatedFtdx10Driver();
        var replacement = new SimulatedFtdx10Driver();
        var allowReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        async ValueTask<IRadioDriver> ConnectAsync(CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1) return first;
            if (attempt == 2) throw new IOException("Radio is still unavailable.");
            await allowReconnect.Task.WaitAsync(cancellationToken);
            return replacement;
        }

        await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
            "retrying-radio",
            ConnectAsync,
            new RadioConnectionSupervisorOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(20)
            });
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("observer"));

        first.SimulateConnectionFailure();

        await WaitUntilAsync(async () =>
            attempts == 3 && (await session.GetSnapshotAsync()).State.Connection == ConnectionStatus.Reconnecting,
            TimeSpan.FromSeconds(2));
        RadioConnectionUnavailableException unavailable = await Assert.ThrowsAsync<RadioConnectionUnavailableException>(
            () => session.RefreshStateAsync().AsTask());
        Assert.Equal(ConnectionStatus.Reconnecting, unavailable.Status);

        allowReconnect.SetResult();
        await WaitUntilAsync(async () =>
            attempts == 3 && (await session.GetSnapshotAsync()).State.Connection == ConnectionStatus.Connected,
            TimeSpan.FromSeconds(2));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task UnchangedLiveRefreshDoesNotIncrementRevision()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("poller"), ClientRole.Observer);
        long revision = (await session.GetSnapshotAsync()).State.Revision;

        RadioState refreshed = await session.RefreshStateAsync();

        Assert.Equal(revision, refreshed.Revision);
    }

    [Fact]
    public async Task ObservationGapPublishesDiagnosticAndForcesFullRefresh()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(
            new ClientIdentity("gap-observer"), ClientRole.Observer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task<RadioEvent> nextEvent = ReadFirstEventAsync(session, timeout.Token);
        int initialReads = context.Driver.ReadStateCount;

        await context.Driver.SimulateObservationGapAsync(12, timeout.Token);

        RadioEvent diagnostic = await nextEvent;
        DeliveryGapObservation gap = Assert.IsType<DeliveryGapObservation>(diagnostic.Payload);
        Assert.Equal(RadioEventKind.Diagnostic, diagnostic.Kind);
        Assert.Equal(RadioDriverObservationKind.DeliveryGap, gap.Kind);
        Assert.Equal(12, gap.DroppedFrames);
        await WaitUntilAsync(
            () => Task.FromResult(context.Driver.ReadStateCount > initialReads),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ObservationOlderThanFullRefreshCannotOverwriteFrequency()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(
            new ClientIdentity("ordering-observer"), ClientRole.Observer);
        await context.Driver.SetFrequencyAsync(VfoId.A, 14_300_000);
        RadioState refreshed = await session.RefreshStateAsync();

        await context.Driver.EmitFrequencyObservationAsync(
            VfoId.A,
            7_100_000,
            refreshed.ObservedAt - TimeSpan.FromSeconds(1));
        await Task.Delay(100);

        RadioState current = (await session.GetSnapshotAsync()).State;
        Assert.Equal(14_300_000, current.FrequenciesHz[VfoId.A]);
        Assert.True(current.ObservedAt >= refreshed.ObservedAt);
    }

    [Fact]
    public async Task TypedControlsAreAuthorizedSerializedAndConfirmed()
    {
        await using TestContext context = await TestContext.CreateAsync(TimeSpan.FromMilliseconds(10));
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("operator"), ClientRole.Operator);

        await session.WriteControlAsync(RadioControlId.AfGain, 75);
        RadioControlValue value = await session.ReadControlAsync(RadioControlId.AfGain);

        Assert.Equal(75, value.Value);
        Assert.Equal(1, context.Driver.MaximumConcurrentOperations);
        Assert.Contains("WriteControl:AfGain:75", context.Driver.CommandLog);
    }

    [Fact]
    public async Task ObserverCanReadMetersButCannotWriteControls()
    {
        await using TestContext context = await TestContext.CreateAsync();
        context.Driver.SetMeterValue(RadioMeterId.SignalStrength, 127);
        await using IRadioSession observer = context.Radio.OpenSession(new ClientIdentity("observer"), ClientRole.Observer);

        RadioMeterReading reading = await observer.ReadMeterAsync(RadioMeterId.SignalStrength);

        Assert.Equal(127, reading.RawValue);
        Assert.Equal(127d / 255d, reading.NormalizedValue, 6);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => observer.WriteControlAsync(RadioControlId.AfGain, 50).AsTask());
    }

    [Fact]
    public async Task FeatureControlsAreAuthorizedSerializedAndConfirmed()
    {
        await using TestContext context = await TestContext.CreateAsync(TimeSpan.FromMilliseconds(5));
        await using IRadioSession observer = context.Radio.OpenSession(new ClientIdentity("observer"), ClientRole.Observer);
        await using IRadioSession operatorSession = context.Radio.OpenSession(new ClientIdentity("operator"), ClientRole.Operator);

        Assert.False((await observer.ReadSwitchAsync(RadioSwitchId.NoiseReduction)).Enabled);
        Assert.Equal("off", (await observer.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => observer.WriteSwitchAsync(RadioSwitchId.NoiseReduction, true).AsTask());

        await operatorSession.WriteSwitchAsync(RadioSwitchId.NoiseReduction, true);
        await operatorSession.WriteChoiceAsync(RadioChoiceId.Attenuator, "6db");

        Assert.True((await operatorSession.ReadSwitchAsync(RadioSwitchId.NoiseReduction)).Enabled);
        Assert.Equal("6db", (await operatorSession.ReadChoiceAsync(RadioChoiceId.Attenuator)).Value);
        Assert.Equal(1, context.Driver.MaximumConcurrentOperations);
    }

    private static async Task<RadioEvent> ReadFirstEventAsync(IRadioSession session, CancellationToken cancellationToken)
    {
        await foreach (RadioEvent radioEvent in session.WatchEventsAsync(cancellationToken))
        {
            return radioEvent;
        }
        throw new InvalidOperationException("The event stream completed without an event.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The condition was not satisfied before the timeout.");
    }

    private sealed class TestContext(ManagedRadio radio, SimulatedFtdx10Driver driver) : IAsyncDisposable
    {
        public ManagedRadio Radio { get; } = radio;
        public SimulatedFtdx10Driver Driver { get; } = driver;

        public static async ValueTask<TestContext> CreateAsync(
            TimeSpan? commandDelay = null,
            TimeProvider? timeProvider = null,
            int pttReadbackLagCount = 0)
        {
            var driver = new SimulatedFtdx10Driver(new SimulatedRadioOptions
            {
                CommandDelay = commandDelay ?? TimeSpan.Zero,
                PttReadbackLagCount = pttReadbackLagCount
            });
            ManagedRadio radio = await ManagedRadio.CreateAsync("sim-ftdx10", driver, timeProvider);
            return new TestContext(radio, driver);
        }

        public ValueTask DisposeAsync() => Radio.DisposeAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class CancellationInsensitiveObservationDriver : IRadioDriver, IRadioObservationSource
    {
        private readonly SimulatedFtdx10Driver _inner = new();
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public RadioCapabilities Capabilities => _inner.Capabilities;

        public ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadStateAsync(cancellationToken);

        public ValueTask SetFrequencyAsync(
            VfoId target, long frequencyHz, CancellationToken cancellationToken = default) =>
            _inner.SetFrequencyAsync(target, frequencyHz, cancellationToken);

        public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
            _inner.SetActiveVfoAsync(vfo, cancellationToken);

        public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
            _inner.SetModeAsync(mode, cancellationToken);

        public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _inner.SetSplitAsync(enabled, cancellationToken);

        public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _inner.SetPttAsync(enabled, cancellationToken);

        public async IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            await _disposed.Task.ConfigureAwait(false);
            yield break;
        }

        public async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _disposed.TrySetResult();
            await _inner.DisposeAsync();
        }
    }

    private sealed class UnsupportedTransmitDriver : IRadioDriver
    {
        private readonly SimulatedFtdx10Driver _inner = new();

        public int SetPttCallCount { get; private set; }

        public RadioCapabilities Capabilities => _inner.Capabilities with
        {
            Transmit = new FeatureDescriptor(CapabilitySupport.Unsupported, FeatureAccess.None)
        };

        public ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadStateAsync(cancellationToken);

        public ValueTask SetFrequencyAsync(
            VfoId target, long frequencyHz, CancellationToken cancellationToken = default) =>
            _inner.SetFrequencyAsync(target, frequencyHz, cancellationToken);

        public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
            _inner.SetActiveVfoAsync(vfo, cancellationToken);

        public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
            _inner.SetModeAsync(mode, cancellationToken);

        public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
            _inner.SetSplitAsync(enabled, cancellationToken);

        public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            SetPttCallCount++;
            return _inner.SetPttAsync(enabled, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
