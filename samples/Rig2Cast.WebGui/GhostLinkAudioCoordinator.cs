using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;

namespace Rig2Cast.WebGui;

public sealed class GhostLinkAudioCoordinator(
    StationConfiguration station,
    ILogger<GhostLinkAudioCoordinator> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, int, int, Exception?> ConnectedLog =
        LoggerMessage.Define<string, int, int>(LogLevel.Information, new EventId(2101, "GhostLinkConnected"),
            "Connected to locked GhostLink server {Host} ({TxPort}/{RxPort}).");
    private static readonly Action<ILogger, int, Exception?> ReconnectLog =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(2102, "GhostLinkReconnect"),
            "GhostLink connection failed; retrying in {DelaySeconds}s.");
    private const int SampleRate = 48000;
    private const int FrameSamples = 960;
    private const int MaxFrameBytes = 64 * 1024;
    private readonly object _browserGate = new();
    private readonly Channel<short[]> _microphone = Channel.CreateBounded<short[]>(
        new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private BrowserConnection? _browser;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _demandCts;
    private bool _desired;
    private long _generation;
    private volatile string _state = "offline";
    private volatile string? _lastError;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!station.IsLocked) return;
        if (IsAlwaysConnected) RequestConnection();
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!ConnectionDesired)
            {
                await SetStateAsync("idle", null, stoppingToken);
                await _wake.WaitAsync(stoppingToken);
                continue;
            }

            CancellationToken demandToken = DemandToken;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, demandToken);
            try
            {
                await SetStateAsync(attempt == 0 ? "connecting" : "reconnecting", null, operation.Token);
                using var fromServer = new TcpClient { NoDelay = true };
                using var toServer = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await Task.WhenAll(
                    fromServer.ConnectAsync(station.Audio.Host, station.Audio.ServerTxPort, timeout.Token).AsTask(),
                    toServer.ConnectAsync(station.Audio.Host, station.Audio.ServerRxPort, timeout.Token).AsTask());

                attempt = 0;
                await SetStateAsync("connected", null, operation.Token);
                ConnectedLog(logger, station.Audio.Host, station.Audio.ServerTxPort, station.Audio.ServerRxPort, null);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                Task receive = ReceiveServerAudioAsync(fromServer.GetStream(), connection.Token);
                Task send = SendMicrophoneAudioAsync(toServer.GetStream(), connection.Token);
                Task completed = await Task.WhenAny(receive, send);
                await completed;
                connection.Cancel();
                await IgnoreCancellationAsync(Task.WhenAll(receive, send));
                throw new IOException("A GhostLink audio direction closed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (!ConnectionDesired)
            {
                attempt = 0;
                while (_microphone.Reader.TryRead(out _)) { }
            }
            catch (Exception ex)
            {
                if (!ConnectionDesired) continue;
                attempt++;
                while (_microphone.Reader.TryRead(out _)) { }
                int delaySeconds = Math.Min(30, 1 << Math.Min(attempt - 1, 4));
                ReconnectLog(logger, delaySeconds, ex);
                await SetStateAsync("reconnecting", ex.Message, operation.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), operation.Token); }
                catch (OperationCanceledException) when (!ConnectionDesired) { attempt = 0; }
            }
        }
        await SetStateAsync("offline", null, CancellationToken.None);
    }

    public async Task AttachBrowserAsync(WebSocket socket, CancellationToken requestAborted)
    {
        AudioBrowserHello hello;
        try { hello = await ReceiveHelloAsync(socket, requestAborted); }
        catch (Exception ex)
        {
            await SendDirectTextAsync(socket, new { state = "error", message = ex.Message }, CancellationToken.None);
            return;
        }

        BrowserConnection connection;
        BrowserConnection? replaced = null;
        lock (_browserGate)
        {
            if (_browser is not null && !StringComparer.Ordinal.Equals(_browser.ClientId, hello.ClientId))
            {
                connection = null!;
            }
            else
            {
                replaced = _browser;
                connection = new BrowserConnection(hello.ClientId, Interlocked.Increment(ref _generation), socket);
                _browser = connection;
            }
        }

        if (connection is null)
        {
            await SendDirectTextAsync(socket, new { state = "busy", message = "Station audio is in use by another browser." }, requestAborted);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Audio is already in use", CancellationToken.None);
            return;
        }

        if (replaced is not null)
        {
            replaced.Socket.Abort();
        }

        RequestConnection();
        await SendTextAsync(connection, new { state = _state == "idle" ? "connecting" : _state, message = _lastError }, requestAborted);
        var pending = new List<short>(FrameSamples * 2);
        byte[] message = new byte[16 * 1024];
        bool explicitStop = false;
        try
        {
            while (!requestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(message, requestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    explicitStop = StringComparer.Ordinal.Equals(result.CloseStatusDescription, "user-stop");
                    await socket.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        CancellationToken.None);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Binary || !station.Access.AllowAudioTransmit || !station.Audio.AllowMicrophone) continue;
                if (!result.EndOfMessage || (result.Count & 1) != 0) throw new InvalidDataException("Invalid browser PCM message.");
                for (int i = 0; i < result.Count; i += 2)
                    pending.Add(BinaryPrimitives.ReadInt16LittleEndian(message.AsSpan(i, 2)));
                while (pending.Count >= FrameSamples)
                {
                    short[] frame = pending.GetRange(0, FrameSamples).ToArray();
                    pending.RemoveRange(0, FrameSamples);
                    _microphone.Writer.TryWrite(frame);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            bool current;
            lock (_browserGate)
            {
                current = _browser?.Generation == connection.Generation;
                if (current && explicitStop) _browser = null;
            }
            pending.Clear();
            while (_microphone.Reader.TryRead(out _)) { }
            if (current)
            {
                if (explicitStop || station.Audio.DisconnectGraceSeconds == 0)
                    ReleaseConnection();
                else
                    _ = ReleaseAfterGraceAsync(connection);
            }
        }
    }

    private bool IsAlwaysConnected =>
        station.Audio.ConnectionPolicy.Equals("AlwaysConnected", StringComparison.OrdinalIgnoreCase);

    private bool ConnectionDesired
    {
        get { lock (_browserGate) return _desired; }
    }

    private CancellationToken DemandToken
    {
        get { lock (_browserGate) return _demandCts?.Token ?? new CancellationToken(canceled: true); }
    }

    private void RequestConnection()
    {
        lock (_browserGate)
        {
            if (_desired) return;
            _desired = true;
            _demandCts?.Dispose();
            _demandCts = new CancellationTokenSource();
        }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private void ReleaseConnection()
    {
        if (IsAlwaysConnected) return;
        lock (_browserGate)
        {
            if (!_desired) return;
            _desired = false;
            _demandCts?.Cancel();
        }
        while (_microphone.Reader.TryRead(out _)) { }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task ReleaseAfterGraceAsync(BrowserConnection connection)
    {
        await Task.Delay(TimeSpan.FromSeconds(station.Audio.DisconnectGraceSeconds));
        bool release = false;
        lock (_browserGate)
        {
            if (_browser?.Generation == connection.Generation)
            {
                _browser = null;
                release = true;
            }
        }
        if (release) ReleaseConnection();
    }

    private async Task ReceiveServerAudioAsync(NetworkStream stream, CancellationToken ct)
    {
        using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
        byte[] header = new byte[4];
        short[] pcm = new short[FrameSamples];
        while (!ct.IsCancellationRequested)
        {
            await ReadExactlyAsync(stream, header, ct);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 2 or > MaxFrameBytes) throw new InvalidDataException($"Invalid GhostLink frame length {length}.");
            byte[] payload = new byte[length];
            await ReadExactlyAsync(stream, payload, ct);
            if (payload[0] != 0x01 || !station.Access.AllowAudioReceive) continue;
            int samples = decoder.Decode(payload.AsSpan(1), pcm, FrameSamples, false);
            byte[] bytes = new byte[samples * 2];
            for (int i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
            BrowserConnection? browser = CurrentBrowser();
            if (browser is not null) await SendBinaryAsync(browser, bytes, ct);
        }
    }

    private async Task SendMicrophoneAudioAsync(NetworkStream stream, CancellationToken ct)
    {
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = station.Audio.OpusBitrate;
        encoder.UseVBR = true;
        encoder.Complexity = 5;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        byte[] opus = new byte[4000];
        await foreach (short[] pcm in _microphone.Reader.ReadAllAsync(ct))
        {
            int length = encoder.Encode(pcm, FrameSamples, opus, opus.Length);
            byte[] header = new byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(header, length + 1);
            header[4] = 0x02;
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(opus.AsMemory(0, length), ct);
        }
    }

    private async Task SetStateAsync(string state, string? error, CancellationToken ct)
    {
        _state = state;
        _lastError = error;
        BrowserConnection? browser = CurrentBrowser();
        if (browser is not null) await SendStateAsync(browser, ct);
    }

    private Task SendStateAsync(BrowserConnection browser, CancellationToken ct) =>
        SendTextAsync(browser, new { state = _state, message = _lastError }, ct);

    private BrowserConnection? CurrentBrowser()
    {
        lock (_browserGate) return _browser;
    }

    private static async Task<AudioBrowserHello> ReceiveHelloAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
            throw new InvalidDataException("The first audio message must identify the browser.");
        AudioBrowserHello hello = JsonSerializer.Deserialize<AudioBrowserHello>(buffer.AsSpan(0, result.Count), JsonOptions)
            ?? throw new InvalidDataException("Audio hello is missing.");
        ArgumentException.ThrowIfNullOrWhiteSpace(hello.ClientId);
        return hello;
    }

    private static async Task SendTextAsync(BrowserConnection browser, object value, CancellationToken ct)
    {
        await browser.SendGate.WaitAsync(ct);
        try
        {
            if (browser.Socket.State == WebSocketState.Open)
                await browser.Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);
        }
        finally { browser.SendGate.Release(); }
    }

    private static async Task SendBinaryAsync(BrowserConnection browser, byte[] value, CancellationToken ct)
    {
        await browser.SendGate.WaitAsync(ct);
        try
        {
            if (browser.Socket.State == WebSocketState.Open)
                await browser.Socket.SendAsync(value, WebSocketMessageType.Binary, true, ct);
        }
        finally { browser.SendGate.Release(); }
    }

    private static Task SendDirectTextAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], ct);
            if (read == 0) throw new EndOfStreamException("GhostLink closed the connection.");
            offset += read;
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private sealed record AudioBrowserHello(string ClientId);
    private sealed record BrowserConnection(string ClientId, long Generation, WebSocket Socket)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
