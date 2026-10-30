using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Rig2Cast.WebGui;

/// <summary>Receives the FTDX10 SPI adapter/relay UDP stream and fans the latest frame out to browsers.</summary>
public sealed class SpectrumStreamService(StationConfiguration station, ILogger<SpectrumStreamService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogReceiverStopped =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, nameof(SpectrumStreamService)), "Spectrum receiver stopped");
    private const int PayloadSize = 4096;
    private const int HeaderSize = 5;
    private const int FragmentCount = 3;
    private const int SpectrumOffset = 30;
    private const int SpectrumLength = 790;
    private static readonly int[] SpansHz = [1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000, 200_000, 500_000, 1_000_000];
    private readonly ConcurrentDictionary<Guid, Channel<SpectrumMessage>> _clients = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private SpectrumStatus _status = new(false, false, "Spectrum disabled", 0, 0, 0, null);
    private SpectrumLevels _levels = new(station.Spectrum.MinimumLevel, station.Spectrum.MaximumLevel);

    public SpectrumStatus GetStatus() => Volatile.Read(ref _status);
    public SpectrumLevels GetLevels() => Volatile.Read(ref _levels);
    public SpectrumLevels SetLevels(int minimumLevel, int maximumLevel)
    {
        if (minimumLevel is < 0 or > 254) throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        if (maximumLevel is < 1 or > 255 || maximumLevel <= minimumLevel)
            throw new ArgumentOutOfRangeException(nameof(maximumLevel), "Maximum level must be greater than minimum level and no more than 255.");
        var levels = new SpectrumLevels(minimumLevel, maximumLevel);
        Volatile.Write(ref _levels, levels);
        return levels;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StationSpectrum settings = station.Spectrum;
        if (!settings.Enabled) return;

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(settings.SourceHost, stoppingToken);
            IPAddress address = addresses.FirstOrDefault(static value => value.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException("Spectrum source does not resolve to an IPv4 address.");
            var remote = new IPEndPoint(address, settings.UdpPort);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ReceiveBufferSize = 1_048_576;
            try { socket.Bind(new IPEndPoint(IPAddress.Any, settings.UdpPort)); }
            catch (SocketException) { socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }

            SetStatus(true, false, $"Connecting to {remote}", 0, 0, 0, null);
            var assembler = new FragmentAssembler();
            Task hello = SendHelloAsync(socket, remote, settings.HelloIntervalSeconds, stoppingToken);
            await ReceiveAsync(socket, remote, assembler, settings.MaximumFramesPerSecond, stoppingToken);
            await hello;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException)
        {
            LogReceiverStopped(logger, exception);
            SetStatus(false, false, $"Spectrum error: {exception.Message}", 0, 0, 0, null);
        }
        finally
        {
            SpectrumStatus current = GetStatus();
            SetStatus(false, false, station.Spectrum.Enabled ? "Spectrum disconnected" : "Spectrum disabled",
                current.Datagrams, current.Frames, current.DroppedFrames, current.LastFrameAt);
        }
    }

    private async Task ReceiveAsync(Socket socket, IPEndPoint remote, FragmentAssembler assembler, int maximumFps, CancellationToken token)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(2048);
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        long datagrams = 0;
        long minimumTicks = TimeSpan.TicksPerSecond / maximumFps;
        long lastPublishedTicks = 0;
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, token);
            if (received.RemoteEndPoint is not IPEndPoint endpoint || !endpoint.Address.Equals(remote.Address)) continue;
            datagrams++;
            if (!assembler.TryAdd(buffer.AsSpan(0, received.ReceivedBytes), out byte[]? payload)) continue;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            SetStatus(true, true, $"Receiving from {remote}", datagrams, assembler.Frames, assembler.DroppedFrames, now);
            if (now.UtcTicks - lastPublishedTicks < minimumTicks) continue;
            lastPublishedTicks = now.UtcTicks;
            SpectrumLevels levels = GetLevels();
            SpectrumMessage? message = Decode(payload!, now, station.Spectrum.Average,
                levels.MinimumLevel, levels.MaximumLevel,
                station.Spectrum.ShowWaterfall, station.Spectrum.WaterfallHeight, station.Spectrum.WaterfallRate);
            if (message is null) continue;
            foreach (Channel<SpectrumMessage> client in _clients.Values) client.Writer.TryWrite(message);
        }
    }

    public async Task StreamAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<SpectrumMessage>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        Guid id = Guid.NewGuid();
        _clients[id] = channel;
        try
        {
            await foreach (SpectrumMessage message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, _json), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally { _clients.TryRemove(id, out _); }
    }

    private static async Task SendHelloAsync(Socket socket, EndPoint remote, int intervalSeconds, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        do { await socket.SendToAsync("HELLO"u8.ToArray(), SocketFlags.None, remote, token); }
        while (await timer.WaitForNextTickAsync(token));
    }

    private static SpectrumMessage? Decode(
        byte[] payload, DateTimeOffset observedAt, int defaultAverage, int minimumLevel, int maximumLevel,
        bool showWaterfall, int waterfallHeight, int waterfallRate)
    {
        byte scopeSetting = payload[3030];
        int spanCode = scopeSetting & 0x0f;
        if (spanCode >= SpansHz.Length) return null;
        int spanHz = SpansHz[spanCode];
        int scopeMode = scopeSetting >> 4;
        int centerHz = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(3042, 4));
        int startHz = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(3046, 4));
        int leftHz = scopeMode is 4 or 8 ? startHz : centerHz - (spanHz / 2);
        bool vfoBActive = (payload[2918] & 0x80) != 0;
        int activeHz = DecodeBcd(payload.AsSpan(2964, 4)) ?? BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(3038, 4));
        int? inactiveHz = DecodeBcd(payload.AsSpan(2988, 4));
        string activeVfo = vfoBActive ? "B" : "A";
        string inactiveVfo = vfoBActive ? "A" : "B";
        byte[] samples = GC.AllocateUninitializedArray<byte>(SpectrumLength);
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)~payload[SpectrumOffset + i];
        return new SpectrumMessage("spectrum", leftHz, leftHz + spanHz, spanHz, samples,
            defaultAverage, minimumLevel, maximumLevel, showWaterfall, waterfallHeight, waterfallRate,
            activeVfo, ModeName(payload[2960]), activeHz, inactiveVfo, ModeName(payload[2984]), inactiveHz,
            payload[2920] != 0, observedAt);
    }

    private static int? DecodeBcd(ReadOnlySpan<byte> data)
    {
        int result = 0;
        foreach (byte value in data)
        {
            int high = value >> 4, low = value & 0x0f;
            if (high > 9 || low > 9) return null;
            result = (result * 100) + (high * 10) + low;
        }
        return result;
    }

    private static string ModeName(byte value) => value switch
    {
        0 => "LSB", 1 => "USB", 2 => "CW-L", 3 => "CW-U", _ => $"0x{value:X2}"
    };

    private void SetStatus(bool running, bool receiving, string message, long datagrams, long frames, long drops, DateTimeOffset? lastFrameAt) =>
        Volatile.Write(ref _status, new SpectrumStatus(running, receiving, message, datagrams, frames, drops, lastFrameAt));

    private sealed class FragmentAssembler
    {
        private readonly byte[]?[] _parts = new byte[FragmentCount][];
        private byte? _sequence;
        public long Frames { get; private set; }
        public long DroppedFrames { get; private set; }

        public bool TryAdd(ReadOnlySpan<byte> datagram, out byte[]? payload)
        {
            payload = null;
            if (datagram.Length < HeaderSize || datagram[0] != 0x66 || datagram[1] != 0xcc || datagram[2] != 0xff || datagram[4] >= FragmentCount) return false;
            byte sequence = datagram[3], fragment = datagram[4];
            if (_sequence != sequence)
            {
                if (_sequence.HasValue && _parts.Any(static part => part is not null)) DroppedFrames++;
                _sequence = sequence;
                Array.Clear(_parts);
            }
            _parts[fragment] = datagram[HeaderSize..].ToArray();
            if (_parts.Any(static part => part is null)) return false;
            int length = _parts.Sum(static part => part!.Length);
            if (length != PayloadSize) { Array.Clear(_parts); return false; }
            payload = GC.AllocateUninitializedArray<byte>(length);
            int offset = 0;
            foreach (byte[] part in _parts.Select(static part => part!)) { part.CopyTo(payload, offset); offset += part.Length; }
            Array.Clear(_parts);
            Frames++;
            return true;
        }
    }
}

public sealed record SpectrumStatus(bool Running, bool Receiving, string Message, long Datagrams, long Frames, long DroppedFrames, DateTimeOffset? LastFrameAt);
public sealed record SpectrumLevels(int MinimumLevel, int MaximumLevel);
public sealed record SpectrumMessage(string Type, int LeftHz, int RightHz, int SpanHz, byte[] Samples,
    int DefaultAverage, int MinimumLevel, int MaximumLevel,
    bool ShowWaterfall, int WaterfallHeight, int WaterfallRate,
    string ActiveVfo, string ActiveMode, int ActiveFrequencyHz, string InactiveVfo, string InactiveMode,
    int? InactiveFrequencyHz, bool SplitEnabled, DateTimeOffset ObservedAt);
