using System.Net;
using System.Net.Sockets;
using System.Text;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Controls;
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
}
