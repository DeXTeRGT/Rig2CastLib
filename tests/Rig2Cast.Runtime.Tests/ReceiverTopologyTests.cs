using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;
using System.Text.Json;

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
            SelectedReceiver = ReceiverId.Indexed(3)
        };

        string json = JsonSerializer.Serialize(state);
        RadioState restored = JsonSerializer.Deserialize<RadioState>(json)!;

        Assert.Equal(ReceiverId.Indexed(3), restored.SelectedReceiver);
        Assert.Equal(50_125_000, restored.Receivers[ReceiverId.Indexed(3)].FrequencyHz);
        Assert.Contains("\"receiver-3\"", json, StringComparison.Ordinal);
    }

    private sealed class ThreeReceiverDriver : IRadioDriver, IRadioReceiverFrequencyDriver, IRadioReceiverModeDriver
    {
        private static readonly ReceiverId Third = ReceiverId.Indexed(3);
        private readonly Dictionary<ReceiverId, (long Frequency, RadioMode Mode)> _state = new()
        {
            [ReceiverId.Main] = (14_200_000, RadioMode.Usb),
            [ReceiverId.Sub] = (7_100_000, RadioMode.Lsb),
            [Third] = (144_300_000, RadioMode.Fm)
        };
        private long _revision;

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
                new Dictionary<RadioChoiceId, ChoiceControlDescriptor>(),
                new Dictionary<RadioMeterId, RadioMeterDescriptor>(),
                new Dictionary<string, object?>())
            {
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
                TransmitReceiver = selected
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
}
