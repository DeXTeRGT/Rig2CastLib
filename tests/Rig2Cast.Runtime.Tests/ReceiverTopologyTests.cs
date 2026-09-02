using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;
using System.Text.Json;
using System.Threading.Channels;

namespace Rig2Cast.Runtime.Tests;

public sealed class ReceiverTopologyTests
{
    [Fact]
    public void ReceiverIdentifiersAreStableExtensibleValues()
    {
        Assert.Equal(ReceiverId.Main, new ReceiverId("MAIN"));
        Assert.Equal("sub", ReceiverId.Sub.ToString());
        Assert.Equal(new ReceiverId("receiver-3"), ReceiverId.Indexed(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("receiver/2")]
    public void InvalidReceiverIdentifiersAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => new ReceiverId(value));
    }

    [Fact]
    public async Task ReceiverOrientedDriverSupportsThreePathsWithoutInventingVfos()
    {
        await using var driver = new ThreeReceiverDriver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("three-receiver", driver);
        await using IRadioSession session = radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);
        ReceiverId third = ReceiverId.Indexed(3);

        await session.SetFrequencyAsync(third, 50_125_000);
        await session.SetModeAsync(third, RadioMode.Cw);
        RadioSnapshot snapshot = await session.GetSnapshotAsync();

        Assert.Empty(snapshot.Capabilities.Vfos.Available);
        Assert.Equal(3, snapshot.Capabilities.Receivers.Available.Count);
        Assert.Equal(50_125_000, snapshot.State.Receivers[third].FrequencyHz);
        Assert.Equal(RadioMode.Cw, snapshot.State.Receivers[third].Mode);
        Assert.Equal(3, snapshot.State.ReceivePaths.Count);
        Assert.All(snapshot.State.ReceivePaths, path => Assert.Null(path.Vfo));
        Assert.Equal(new RadioSignalPath(ReceiverId.Main, null), snapshot.State.TransmitPath);
    }

    [Fact]
    public async Task LegacyVfoOperationFailsInsteadOfGuessingOnReceiverOnlyTopology()
    {
        await using var driver = new ThreeReceiverDriver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("three-receiver", driver);
        await using IRadioSession session = radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.SetFrequencyAsync(VfoId.A, 14_250_000).AsTask());

        RadioState state = (await session.GetSnapshotAsync()).State;
        Assert.Equal(14_200_000, state.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(7_100_000, state.Receivers[ReceiverId.Sub].FrequencyHz);
        Assert.Equal(144_300_000, state.Receivers[ReceiverId.Indexed(3)].FrequencyHz);
    }

    [Fact]
    public async Task ReceiverTargetedChoiceAndPassbandUseAddressedReceiverMode()
    {
        await using var driver = new ThreeReceiverDriver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("three-receiver", driver);
        await using IRadioSession session = radio.OpenSession(
            new ClientIdentity("operator"), ClientRole.Operator);

        await session.WriteChoiceAsync(RadioChoiceId.FilterWidth, ReceiverId.Sub, "wide-lsb");
        await session.SetPassbandAsync(ReceiverId.Sub, 2800);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WriteChoiceAsync(RadioChoiceId.FilterWidth, ReceiverId.Main, "wide-lsb").AsTask());

        Assert.Equal(1, driver.ChoiceWriteCount);
        Assert.Equal(1, driver.PassbandWriteCount);
    }

    [Fact]
    public async Task ReceiverAndSignalPathObservationsUpdateOnlyAddressedState()
    {
        await using var driver = new ThreeReceiverDriver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("three-receiver", driver);
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("observer"));
        ReceiverId third = ReceiverId.Indexed(3);
        DateTimeOffset observedAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);

        await driver.EmitObservationAsync(new ReceiverFrequencyChangedObservation(
            observedAt, "receiver-3-frequency", third, 50_200_000));
        await driver.EmitObservationAsync(new ReceiverModeChangedObservation(
            observedAt, "sub-mode", ReceiverId.Sub, RadioMode.Cw));
        await driver.EmitObservationAsync(new ReceivePathsChangedObservation(
            observedAt, "receive-paths", [new(ReceiverId.Sub, null), new(third, null)]));
        await driver.EmitObservationAsync(new TransmitPathChangedObservation(
            observedAt, "transmit-path", new RadioSignalPath(third, null)));

        await WaitUntilAsync(async () =>
        {
            RadioState state = (await session.GetSnapshotAsync()).State;
            return state.Receivers[third].FrequencyHz == 50_200_000 &&
                   state.Receivers[ReceiverId.Sub].Mode == RadioMode.Cw &&
                   state.ReceivePaths.Count == 2 && state.TransmitPath?.Receiver == third;
        });

        RadioState updated = (await session.GetSnapshotAsync()).State;
        Assert.Equal(14_200_000, updated.Receivers[ReceiverId.Main].FrequencyHz);
        Assert.Equal(RadioMode.Usb, updated.Receivers[ReceiverId.Main].Mode);
        Assert.Equal(third, updated.TransmitReceiver);
    }

    [Fact]
    public void ReceiverStateJsonRoundTripPreservesStableDictionaryKeys()
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        var state = new RadioState(
            1, ConnectionStatus.Connected, new Dictionary<VfoId, long>(),
            VfoId.Current, RadioMode.Usb, false, false, observedAt)
        {
            Receivers = new Dictionary<ReceiverId, RadioReceiverState>
            {
                [ReceiverId.Main] = new(ReceiverId.Main, true, null, 14_200_000, RadioMode.Usb, 2400, observedAt),
                [ReceiverId.Indexed(3)] = new(ReceiverId.Indexed(3), true, null, 50_125_000, RadioMode.Cw, 500, observedAt)
            },
            SelectedReceiver = ReceiverId.Indexed(3),
            ReceivePaths =
            [
                new RadioSignalPath(ReceiverId.Main, VfoId.A),
                new RadioSignalPath(ReceiverId.Indexed(3), null)
            ],
            TransmitPath = new RadioSignalPath(ReceiverId.Indexed(3), null)
        };

        string json = JsonSerializer.Serialize(state);
        RadioState restored = JsonSerializer.Deserialize<RadioState>(json)!;

        Assert.Equal(ReceiverId.Indexed(3), restored.SelectedReceiver);
        Assert.Equal(50_125_000, restored.Receivers[ReceiverId.Indexed(3)].FrequencyHz);
        Assert.Equal(state.ReceivePaths, restored.ReceivePaths);
        Assert.Equal(state.TransmitPath, restored.TransmitPath);
        Assert.Contains("\"receiver-3\"", json, StringComparison.Ordinal);
    }

    private sealed class ThreeReceiverDriver : IRadioDriver, IRadioReceiverFrequencyDriver, IRadioReceiverModeDriver,
        IRadioReceiverChoiceDriver, IRadioReceiverPassbandDriver, IRadioObservationSource
    {
        private static readonly ReceiverId Third = ReceiverId.Indexed(3);
        private readonly Dictionary<ReceiverId, (long Frequency, RadioMode Mode)> _state = new()
        {
            [ReceiverId.Main] = (14_200_000, RadioMode.Usb),
            [ReceiverId.Sub] = (7_100_000, RadioMode.Lsb),
            [Third] = (144_300_000, RadioMode.Fm)
        };
        private long _revision;
        private readonly Channel<RadioDriverObservation> _observations = Channel.CreateUnbounded<RadioDriverObservation>();

        public int ChoiceWriteCount { get; private set; }

        public int PassbandWriteCount { get; private set; }

        public ThreeReceiverDriver()
        {
            var readWrite = new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read | FeatureAccess.Write);
            HashSet<ReceiverId> receivers = _state.Keys.ToHashSet();
            var range = new FrequencyRange(100_000, 500_000_000, true, false);
            Capabilities = new RadioCapabilities(
                1,
                "Synthetic",
                "Three receiver",
                "rig2cast.tests.three-receiver",
                "1.0.0",
                new VfoCapability(
                    new HashSet<VfoId>(),
                    new FeatureDescriptor(CapabilitySupport.Unsupported, FeatureAccess.None),
                    new FeatureDescriptor(CapabilitySupport.Unsupported, FeatureAccess.None)),
                new FrequencyCapability(readWrite, new HashSet<VfoId>(), [range], 1)
                {
                    ReceiverTargets = receivers,
                    RangesByReceiver = receivers.ToDictionary(
                        receiver => receiver,
                        _ => (IReadOnlyList<FrequencyRange>)[range])
                },
                new ModeCapability(readWrite, new HashSet<RadioMode> { RadioMode.Lsb, RadioMode.Usb, RadioMode.Cw, RadioMode.Fm })
                {
                    ReceiverTargets = receivers,
                    ValuesByReceiver = receivers.ToDictionary(
                        receiver => receiver,
                        _ => (IReadOnlySet<RadioMode>)new HashSet<RadioMode>
                            { RadioMode.Lsb, RadioMode.Usb, RadioMode.Cw, RadioMode.Fm })
                },
                new FeatureDescriptor(CapabilitySupport.Unsupported, FeatureAccess.None),
                new Dictionary<RadioControlId, NumericControlDescriptor>(),
                new Dictionary<RadioSwitchId, SwitchControlDescriptor>(),
                new Dictionary<RadioChoiceId, ChoiceControlDescriptor>
                {
                    [RadioChoiceId.FilterWidth] = new(
                        RadioChoiceId.FilterWidth,
                        "Filter width",
                        readWrite,
                        new Dictionary<string, RadioChoiceOption>
                        {
                            ["wide-usb"] = new("wide-usb", "Wide USB", ApplicableModes: new HashSet<RadioMode> { RadioMode.Usb }),
                            ["wide-lsb"] = new("wide-lsb", "Wide LSB", ApplicableModes: new HashSet<RadioMode> { RadioMode.Lsb })
                        })
                    {
                        ReceiverTargets = receivers
                    }
                },
                new Dictionary<RadioMeterId, RadioMeterDescriptor>(),
                new Dictionary<string, object?>())
            {
                Passband = new PassbandCapability(
                    readWrite,
                    new Dictionary<RadioMode, PassbandConstraint>
                    {
                        [RadioMode.Usb] = new(2400, 2400, 1, [2400]),
                        [RadioMode.Lsb] = new(2800, 2800, 1, [2800])
                    })
                {
                    ReceiverTargets = receivers
                },
                Receivers = new ReceiverTopologyCapability(
                    receivers.ToDictionary(
                        receiver => receiver,
                        receiver => new ReceiverCapability(
                            receiver, receiver.ToString(), new HashSet<VfoId>(),
                            SupportsSimultaneousReception: true,
                            HasIndependentFrequency: true,
                            HasIndependentMode: true)),
                    new FeatureDescriptor(CapabilitySupport.Unsupported, FeatureAccess.None))
            };
        }

        public RadioCapabilities Capabilities { get; }

        public IAsyncEnumerable<RadioDriverObservation> WatchObservationsAsync(
            CancellationToken cancellationToken = default) =>
            _observations.Reader.ReadAllAsync(cancellationToken);

        public ValueTask EmitObservationAsync(RadioDriverObservation observation) =>
            _observations.Writer.WriteAsync(observation);

        public ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            ReceiverId selected = ReceiverId.Main;
            return ValueTask.FromResult(new RadioState(
                Interlocked.Increment(ref _revision),
                ConnectionStatus.Connected,
                new Dictionary<VfoId, long>(),
                VfoId.Current,
                _state[selected].Mode,
                false,
                false,
                observedAt)
            {
                Receivers = _state.ToDictionary(
                    pair => pair.Key,
                    pair => new RadioReceiverState(
                        pair.Key, true, null, pair.Value.Frequency, pair.Value.Mode, null, observedAt)),
                SelectedReceiver = selected,
                TransmitReceiver = selected,
                ReceivePaths = _state.Keys
                    .Select(receiver => new RadioSignalPath(receiver, null))
                    .ToArray(),
                TransmitPath = new RadioSignalPath(selected, null)
            });
        }

        public ValueTask SetFrequencyAsync(
            ReceiverId receiver, long frequencyHz, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long _, RadioMode mode) = _state[receiver];
            _state[receiver] = (frequencyHz, mode);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetModeAsync(
            ReceiverId receiver, RadioMode mode, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long frequency, RadioMode _) = _state[receiver];
            _state[receiver] = (frequency, mode);
            return ValueTask.CompletedTask;
        }

        public ValueTask<RadioChoiceValue> ReadChoiceAsync(
            RadioChoiceId control, ReceiverId receiver, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RadioChoiceValue(
                control, "wide-lsb", DateTimeOffset.UtcNow) { Receiver = receiver });

        public ValueTask WriteChoiceAsync(
            RadioChoiceId control, ReceiverId receiver, string value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChoiceWriteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<RadioPassbandValue> ReadPassbandAsync(
            ReceiverId receiver, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RadioPassbandValue(
                receiver == ReceiverId.Sub ? 2800 : 2400, DateTimeOffset.UtcNow) { Receiver = receiver });

        public ValueTask SetPassbandAsync(
            ReceiverId receiver, int widthHz, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PassbandWriteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetFrequencyAsync(VfoId target, long frequencyHz, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());
        public ValueTask SetActiveVfoAsync(VfoId vfo, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());
        public ValueTask SetModeAsync(RadioMode mode, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());
        public ValueTask SetSplitAsync(bool enabled, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());
        public ValueTask SetPttAsync(bool enabled, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!await predicate())
            await Task.Delay(20, timeout.Token);
    }
}
