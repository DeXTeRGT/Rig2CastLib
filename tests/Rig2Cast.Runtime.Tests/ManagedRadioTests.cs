using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Capabilities;

namespace Rig2Cast.Runtime.Tests;

public sealed class ManagedRadioTests
{
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
        Assert.Contains("SetActiveVfo:B", context.Driver.CommandLog);
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
    public async Task UnchangedLiveRefreshDoesNotIncrementRevision()
    {
        await using TestContext context = await TestContext.CreateAsync();
        await using IRadioSession session = context.Radio.OpenSession(new ClientIdentity("poller"), ClientRole.Observer);
        long revision = (await session.GetSnapshotAsync()).State.Revision;

        RadioState refreshed = await session.RefreshStateAsync();

        Assert.Equal(revision, refreshed.Revision);
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

        public static async ValueTask<TestContext> CreateAsync(TimeSpan? commandDelay = null)
        {
            var driver = new SimulatedFtdx10Driver(new SimulatedRadioOptions
            {
                CommandDelay = commandDelay ?? TimeSpan.Zero
            });
            ManagedRadio radio = await ManagedRadio.CreateAsync("sim-ftdx10", driver);
            return new TestContext(radio, driver);
        }

        public ValueTask DisposeAsync() => Radio.DisposeAsync();
    }
}
