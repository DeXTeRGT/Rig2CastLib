using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;

namespace Rig2Cast.WebGui;

public sealed record AudioStreamOptions(string Host, int TxPort, int RxPort, int Bitrate = 16000);

/// <summary>Bridges browser PCM audio to the GhostLink-compatible dual TCP/Opus protocol.</summary>
public static class AudioStreamBridge
{
    private const int SampleRate = 48000;
    private const int FrameSamples = 960; // 20 ms, mono
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(WebSocket socket, CancellationToken requestAborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        try
        {
            AudioStreamOptions options = await ReceiveOptionsAsync(socket, lifetime.Token);
            Validate(options);

            using var fromServer = new TcpClient { NoDelay = true };
            using var toServer = new TcpClient { NoDelay = true };
            await Task.WhenAll(
                fromServer.ConnectAsync(options.Host, options.TxPort, lifetime.Token).AsTask(),
                toServer.ConnectAsync(options.Host, options.RxPort, lifetime.Token).AsTask());

            await SendTextAsync(socket, "{\"state\":\"connected\"}", lifetime.Token);
            Task receive = RelayServerAudioAsync(fromServer.GetStream(), socket, lifetime.Token);
            Task send = RelayBrowserAudioAsync(socket, toServer.GetStream(), options.Bitrate, lifetime.Token);
            await Task.WhenAny(receive, send);
            lifetime.Cancel();
            await IgnoreCancellationAsync(Task.WhenAll(receive, send));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (socket.State == WebSocketState.Open)
            {
                string json = JsonSerializer.Serialize(new { state = "error", message = ex.Message });
                await SendTextAsync(socket, json, CancellationToken.None);
            }
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Audio stream stopped", CancellationToken.None);
        }
    }

    private static async Task<AudioStreamOptions> ReceiveOptionsAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
            throw new InvalidDataException("The first audio message must be a single JSON configuration message.");
        return JsonSerializer.Deserialize<AudioStreamOptions>(buffer.AsSpan(0, result.Count), JsonOptions)
            ?? throw new InvalidDataException("Audio configuration is missing.");
    }

    private static void Validate(AudioStreamOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        if (options.TxPort is < 1 or > 65535 || options.RxPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Audio ports must be between 1 and 65535.");
        if (options.Bitrate is < 6000 or > 128000)
            throw new ArgumentOutOfRangeException(nameof(options), "Opus bitrate must be between 6000 and 128000.");
    }

    private static async Task RelayBrowserAudioAsync(WebSocket socket, NetworkStream stream, int bitrate, CancellationToken ct)
    {
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitrate;
        encoder.UseVBR = true;
        encoder.Complexity = 5;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        var pending = new List<short>(FrameSamples * 2);
        byte[] message = new byte[16 * 1024];
        byte[] opus = new byte[4000];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(message, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Binary) continue;
            if (!result.EndOfMessage) throw new InvalidDataException("Audio WebSocket messages must not be fragmented.");
            if ((result.Count & 1) != 0) throw new InvalidDataException("Browser PCM payload has an odd byte count.");
            for (int i = 0; i < result.Count; i += 2)
                pending.Add(BinaryPrimitives.ReadInt16LittleEndian(message.AsSpan(i, 2)));
            while (pending.Count >= FrameSamples)
            {
                short[] pcm = pending.GetRange(0, FrameSamples).ToArray();
                pending.RemoveRange(0, FrameSamples);
                int length = encoder.Encode(pcm, FrameSamples, opus, opus.Length);
                await WriteGhostLinkFrameAsync(stream, 0x02, opus.AsMemory(0, length), ct);
            }
        }
    }

    private static async Task RelayServerAudioAsync(NetworkStream stream, WebSocket socket, CancellationToken ct)
    {
        using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
        byte[] header = new byte[4];
        short[] pcm = new short[FrameSamples];
        while (!ct.IsCancellationRequested)
        {
            await ReadExactlyAsync(stream, header, ct);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 2 or > MaxFrameBytes) throw new InvalidDataException($"Invalid audio frame length {length}.");
            byte[] payload = new byte[length];
            await ReadExactlyAsync(stream, payload, ct);
            if (payload[0] != 0x01) continue;
            int samples = decoder.Decode(payload.AsSpan(1), pcm, FrameSamples, false);
            byte[] bytes = new byte[samples * 2];
            for (int i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct);
        }
    }

    private static async Task WriteGhostLinkFrameAsync(NetworkStream stream, byte streamId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] header = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length + 1);
        header[4] = streamId;
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], ct);
            if (read == 0) throw new EndOfStreamException("Audio server closed the connection.");
            offset += read;
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }
}
