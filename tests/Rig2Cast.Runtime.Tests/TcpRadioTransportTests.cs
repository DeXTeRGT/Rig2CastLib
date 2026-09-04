using System.Net;
using System.Net.Sockets;
using Rig2Cast.Protocols.Civ;
using Rig2Cast.Transports.Tcp;

namespace Rig2Cast.Runtime.Tests;

public sealed class TcpRadioTransportTests
{
    [Fact]
    public async Task CarriesArbitraryBytesUnchangedAndAcceptsFragmentedReads()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await using var transport = new TcpRadioTransport(Options(port));

        await transport.ConnectAsync();
        using TcpClient peer = await accept;
        NetworkStream peerStream = peer.GetStream();
        byte[] command = [0x00, 0xFE, 0xFE, 0x70, 0xE0, 0x03, 0xFD, 0xFF];
        await transport.WriteAsync(command);

        byte[] received = new byte[command.Length];
        await peerStream.ReadExactlyAsync(received);
        Assert.Equal(command, received);

        await peerStream.WriteAsync(new byte[] { 0xFE, 0xFE, 0xE0 });
        await peerStream.WriteAsync(new byte[] { 0x70, 0x03, 0x00, 0xFD });
        byte[] response = new byte[7];
        await ReadExactlyAsync(transport, response);
        Assert.Equal(new byte[] { 0xFE, 0xFE, 0xE0, 0x70, 0x03, 0x00, 0xFD }, response);
    }

    [Fact]
    public async Task ClosingTransportReleasesBlockedRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await using var transport = new TcpRadioTransport(Options(port));
        await transport.ConnectAsync();
        using TcpClient peer = await accept;
        Task<int> blockedRead = transport.ReadAsync(new byte[16], CancellationToken.None).AsTask();

        await transport.DisconnectAsync();

        Exception? failure = await Record.ExceptionAsync(async () =>
            _ = await blockedRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(failure is TimeoutException, "Disconnect did not release the blocked TCP read.");
    }

    [Fact]
    public async Task CivSessionQueriesRawTcpWithoutAdditionalFraming()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await using var transport = new TcpRadioTransport(Options(port));
        await transport.ConnectAsync();
        using TcpClient peer = await accept;
        NetworkStream peerStream = peer.GetStream();
        await using var session = new CivSession(transport, responseTimeout: TimeSpan.FromSeconds(1));

        Task server = Task.Run(async () =>
        {
            byte[] request = new byte[6];
            await peerStream.ReadExactlyAsync(request);
            Assert.Equal(new byte[] { 0xFE, 0xFE, 0x70, 0xE0, 0x03, 0xFD }, request);
            await peerStream.WriteAsync(new byte[] { 0xFE, 0xFE, 0xE0, 0x70 });
            await peerStream.WriteAsync(new byte[] { 0x03, 0x00, 0x00, 0x20, 0x14, 0x00, 0xFD });
        });

        CivFrame response = await session.QueryAsync(
            new CivFrame(0x70, 0xE0, [0x03]), new byte[] { 0x03 });

        await server;
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x20, 0x14, 0x00 }, response.Message.ToArray());
    }

    [Fact]
    public async Task DisconnectedTransportCanReconnectWithFreshSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var transport = new TcpRadioTransport(Options(port));

        Task<TcpClient> firstAccept = listener.AcceptTcpClientAsync();
        await transport.ConnectAsync();
        using TcpClient firstPeer = await firstAccept;
        await transport.DisconnectAsync();

        Task<TcpClient> secondAccept = listener.AcceptTcpClientAsync();
        await transport.ConnectAsync();
        using TcpClient secondPeer = await secondAccept;

        Assert.True(transport.IsConnected);
        Assert.True(secondPeer.Connected);
    }

    private static TcpRadioTransportOptions Options(int port) => new()
    {
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    };

    private static async Task ReadExactlyAsync(TcpRadioTransport transport, Memory<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await transport.ReadAsync(buffer[offset..]);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }
}
