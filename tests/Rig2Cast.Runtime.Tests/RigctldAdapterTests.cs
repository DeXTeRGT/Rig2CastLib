using System.Net;
using System.Net.Sockets;
using System.Text;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Adapters.Rigctld;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;

namespace Rig2Cast.Runtime.Tests;

public sealed class RigctldAdapterTests
{
    [Fact]
    public void ParsesShortLongAndExtendedCommands()
    {
        RigctldRequest shortCommand = RigctldProtocol.Parse("F 14250000");
        RigctldRequest longCommand = RigctldProtocol.Parse("\\get_freq");
        RigctldRequest extended = RigctldProtocol.Parse("+M USB 2400");

        Assert.Equal("set_freq", shortCommand.Command);
        Assert.Equal(["14250000"], shortCommand.Arguments);
        Assert.Equal("get_freq", longCommand.Command);
        Assert.True(extended.Extended);
        Assert.Equal("set_mode", extended.Command);
    }

    [Fact]
    public void FormatsDefaultAndExtendedResponses()
    {
        RigctldRequest normal = RigctldProtocol.Parse("m");
        RigctldRequest extended = RigctldProtocol.Parse("+m");
        var result = new RigctldResult("get_mode", [new("Mode", "USB"), new("Passband", "2400")]);

        Assert.Equal("USB\n2400\n", RigctldProtocol.Format(normal, result));
        Assert.Equal("get_mode:\nMode: USB\nPassband: 2400\nRPRT 0\n", RigctldProtocol.Format(extended, result));
    }

    [Fact]
    public async Task MultipleTcpClientsUseOneSerializedRadioRuntime()
    {
        var driver = new SimulatedFtdx10Driver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", driver);
        await using var server = new RigctldServer(
            new RigctldServerOptions { Address = IPAddress.Loopback, Port = 0, MaximumClients = 2 },
            id => radio.OpenSession(new ClientIdentity(id), ClientRole.Observer));
        server.Start();
        int port = Assert.IsType<IPEndPoint>(server.LocalEndpoint).Port;

        Task<string> first = SendCommandAsync(port, "f\n");
        Task<string> second = SendCommandAsync(port, "v\n");
        string[] responses = await Task.WhenAll(first, second);

        Assert.Contains("14200000\n", responses);
        Assert.Contains("VFOA\n", responses);
        Assert.Equal(1, driver.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task ConcurrentTcpReadsShareOneExpiredStateRefresh()
    {
        var driver = new SimulatedFtdx10Driver(new SimulatedRadioOptions
        {
            CommandDelay = TimeSpan.FromMilliseconds(75)
        });
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", driver);
        int initialReads = driver.CommandLog.Count(command => command == "ReadState");
        await using var server = new RigctldServer(
            new RigctldServerOptions { Address = IPAddress.Loopback, Port = 0, MaximumClients = 2 },
            id => radio.OpenSession(new ClientIdentity(id), ClientRole.Observer));
        server.Start();
        int port = Assert.IsType<IPEndPoint>(server.LocalEndpoint).Port;

        await Task.Delay(300);
        await Task.WhenAll(
            SendCommandAsync(port, "f\n"),
            SendCommandAsync(port, "v\n"));

        Assert.Equal(initialReads + 1, driver.CommandLog.Count(command => command == "ReadState"));
        Assert.Equal(1, driver.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task ReadOnlyServerRejectsSettersAndPttSetterIsAlwaysUnavailable()
    {
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", new SimulatedFtdx10Driver());
        await using var session = radio.OpenSession(new ClientIdentity("test"), ClientRole.Operator);

        var readOnly = new RigctldSessionHandler(session);
        RigctldResult frequency = await readOnly.ExecuteAsync(RigctldProtocol.Parse("F 14250000"));
        var writable = new RigctldSessionHandler(session, writesEnabled: true);
        RigctldResult ptt = await writable.ExecuteAsync(RigctldProtocol.Parse("T 1"));

        Assert.Equal(RigctldError.Rejected, frequency.ErrorCode);
        Assert.Equal(RigctldError.NotAvailable, ptt.ErrorCode);
    }

    [Fact]
    public async Task ModeGetReportsNativeFilterWidth()
    {
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", new SimulatedFtdx10Driver());
        await using var session = radio.OpenSession(new ClientIdentity("test"), ClientRole.Operator);
        await session.WriteChoiceAsync(RadioChoiceId.FilterWidth, "2400hz");
        var handler = new RigctldSessionHandler(session, writesEnabled: true);

        RigctldResult result = await handler.ExecuteAsync(RigctldProtocol.Parse("m"));

        Assert.Equal(RigctldError.Ok, result.ErrorCode);
        Assert.Equal("USB", result.Values[0].Value);
        Assert.Equal("2400", result.Values[1].Value);
    }

    [Fact]
    public async Task ModeSetChoosesClosestApplicableNativeFilterWidthAtomically()
    {
        var driver = new SimulatedFtdx10Driver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", driver);
        await using var session = radio.OpenSession(new ClientIdentity("test"), ClientRole.Controller);
        var handler = new RigctldSessionHandler(session, writesEnabled: true);

        RigctldResult result = await handler.ExecuteAsync(RigctldProtocol.Parse("M USB 2430"));

        Assert.Equal(RigctldError.Ok, result.ErrorCode);
        Assert.Equal(RadioMode.Usb, (await session.GetSnapshotAsync()).State.Mode);
        Assert.Equal("2450hz", (await session.ReadChoiceAsync(RadioChoiceId.FilterWidth)).Value);
        Assert.Equal(1, driver.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task ZeroPassbandSelectsNativeDefault()
    {
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", new SimulatedFtdx10Driver());
        await using var session = radio.OpenSession(new ClientIdentity("test"), ClientRole.Controller);
        await session.WriteChoiceAsync(RadioChoiceId.FilterWidth, "3000hz");
        var handler = new RigctldSessionHandler(session, writesEnabled: true);

        RigctldResult result = await handler.ExecuteAsync(RigctldProtocol.Parse("M USB 0"));

        Assert.Equal(RigctldError.Ok, result.ErrorCode);
        Assert.Equal("default", (await session.ReadChoiceAsync(RadioChoiceId.FilterWidth)).Value);
    }

    [Fact]
    public async Task SplitCommandsUseReportedAndRequestedTransmitVfo()
    {
        var driver = new SimulatedFtdx10Driver();
        await using ManagedRadio radio = await ManagedRadio.CreateAsync("ftdx10", driver);
        await using var session = radio.OpenSession(new ClientIdentity("test"), ClientRole.Controller);
        var handler = new RigctldSessionHandler(session, writesEnabled: true);

        RigctldResult read = await handler.ExecuteAsync(RigctldProtocol.Parse("s"));
        RigctldResult written = await handler.ExecuteAsync(RigctldProtocol.Parse("S 1 VFOB"));

        Assert.Equal("VFOB", read.Values[1].Value);
        Assert.Equal(RigctldError.Ok, written.ErrorCode);
        Assert.Contains("SetSplit:True:B", driver.CommandLog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonRepresentableReceiverTopologiesFailInsteadOfGuessingVfoA(bool multipleReceivePaths)
    {
        await using ManagedRadio radio = await ManagedRadio.CreateAsync(
            "receiver-topology", new RigctldTopologyDriver(multipleReceivePaths));
        await using IRadioSession session = radio.OpenSession(new ClientIdentity("test"), ClientRole.Observer);
        var handler = new RigctldSessionHandler(session);

        RigctldResult frequency = await handler.ExecuteAsync(RigctldProtocol.Parse("f"));
        RigctldResult vfo = await handler.ExecuteAsync(RigctldProtocol.Parse("v"));
        RigctldResult split = await handler.ExecuteAsync(RigctldProtocol.Parse("s"));

        Assert.Equal(RigctldError.NotAvailable, frequency.ErrorCode);
        Assert.Equal(RigctldError.NotAvailable, vfo.ErrorCode);
        Assert.Equal(RigctldError.NotAvailable, split.ErrorCode);
    }

    private static async Task<string> SendCommandAsync(int port, string command)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command));
        var buffer = new byte[128];
        int read = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private sealed class RigctldTopologyDriver(bool multipleReceivePaths) : IRadioDriver
    {
        private static readonly FeatureDescriptor Unsupported =
            new(CapabilitySupport.Unsupported, FeatureAccess.None);

        public RadioCapabilities Capabilities { get; } = new(
            1,
            "Synthetic",
            "Rigctld topology",
            "rig2cast.tests.rigctld-topology",
            "1.0.0",
            new VfoCapability(
                multipleReceivePaths ? new HashSet<VfoId> { VfoId.A, VfoId.B } : new HashSet<VfoId>(),
                Unsupported,
                Unsupported),
            new FrequencyCapability(
                Unsupported,
                multipleReceivePaths ? new HashSet<VfoId> { VfoId.A, VfoId.B } : new HashSet<VfoId>(),
                [new FrequencyRange(100_000, 500_000_000, true, false)]),
            new ModeCapability(Unsupported, new HashSet<RadioMode> { RadioMode.Usb }),
            Unsupported,
            new Dictionary<RadioControlId, NumericControlDescriptor>(),
            new Dictionary<RadioSwitchId, SwitchControlDescriptor>(),
            new Dictionary<RadioChoiceId, ChoiceControlDescriptor>(),
            new Dictionary<RadioMeterId, RadioMeterDescriptor>(),
            new Dictionary<string, object?>())
        {
            Receivers = new ReceiverTopologyCapability(
                new Dictionary<ReceiverId, ReceiverCapability>
                {
                    [ReceiverId.Main] = new(ReceiverId.Main, "Main", new HashSet<VfoId> { VfoId.A }),
                    [ReceiverId.Sub] = new(ReceiverId.Sub, "Sub", new HashSet<VfoId> { VfoId.B })
                },
                Unsupported)
        };

        public ValueTask<RadioState> ReadStateAsync(CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyDictionary<VfoId, long> frequencies = multipleReceivePaths
                ? new Dictionary<VfoId, long> { [VfoId.A] = 14_200_000, [VfoId.B] = 7_100_000 }
                : new Dictionary<VfoId, long>();
            return ValueTask.FromResult(new RadioState(
                1, ConnectionStatus.Connected, frequencies,
                multipleReceivePaths ? VfoId.A : VfoId.Current,
                RadioMode.Usb, false, false, now)
            {
                Receivers = new Dictionary<ReceiverId, RadioReceiverState>
                {
                    [ReceiverId.Main] = new(ReceiverId.Main, true,
                        multipleReceivePaths ? VfoId.A : null, 14_200_000, RadioMode.Usb, null, now),
                    [ReceiverId.Sub] = new(ReceiverId.Sub, true,
                        multipleReceivePaths ? VfoId.B : null, 7_100_000, RadioMode.Lsb, null, now)
                },
                SelectedReceiver = ReceiverId.Main,
                ReceivePaths = multipleReceivePaths
                    ? [new(ReceiverId.Main, VfoId.A), new(ReceiverId.Sub, VfoId.B)]
                    : [new(ReceiverId.Main, null)],
                TransmitReceiver = null,
                TransmitPath = null
            });
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
