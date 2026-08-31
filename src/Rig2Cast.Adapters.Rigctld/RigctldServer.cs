using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Rig2Cast.Abstractions.Sessions;

namespace Rig2Cast.Adapters.Rigctld;

public sealed record RigctldServerOptions
{
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 4532;
    public int MaximumClients { get; init; } = 32;
    public int MaximumCommandLength { get; init; } = 4096;
    public bool WritesEnabled { get; init; }
}

public sealed class RigctldServer(
    RigctldServerOptions options,
    Func<string, IRadioSession> sessionFactory) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(options.Address, options.Port);
    private readonly SemaphoreSlim _clientSlots = new(options.MaximumClients, options.MaximumClients);
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;
    private long _nextClientId;
    private int _disposed;

    public IPEndPoint? LocalEndpoint => _listener.LocalEndpoint as IPEndPoint;
    public int ActiveClientCount => _clients.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_acceptLoop is not null) throw new InvalidOperationException("The server is already running.");
        if (options.MaximumClients <= 0 || options.MaximumCommandLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _listener.Start(options.MaximumClients);
        _acceptLoop = AcceptClientsAsync(_stopping.Token);
    }

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _clientSlots.WaitAsync(token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch { _clientSlots.Release(); throw; }

                long id = Interlocked.Increment(ref _nextClientId);
                Task task = ServeClientAsync(id, client, token);
                _clients[id] = task;
                _ = task.ContinueWith(
                    completed => { _clients.TryRemove(id, out Task? _); _clientSlots.Release(); },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private async Task ServeClientAsync(long id, TcpClient client, CancellationToken token)
    {
        using (client)
        await using (IRadioSession session = sessionFactory($"rigctld-{id}"))
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            var handler = new RigctldSessionHandler(session, options.WritesEnabled);
            while (!token.IsCancellationRequested)
            {
                string? line;
                try { line = await ReadLineAsync(stream, options.MaximumCommandLength, token).ConfigureAwait(false); }
                catch (InvalidDataException)
                {
                    byte[] error = Encoding.ASCII.GetBytes("RPRT -1\n");
                    await stream.WriteAsync(error, token).ConfigureAwait(false);
                    break;
                }
                if (line is null) break;

                RigctldRequest request;
                RigctldResult result;
                try
                {
                    request = RigctldProtocol.Parse(line);
                    result = await handler.ExecuteAsync(request, token).ConfigureAwait(false);
                }
                catch (FormatException)
                {
                    request = new("unknown", [], false, '\n');
                    result = new("unknown", [], RigctldError.InvalidParameter);
                }

                byte[] response = Encoding.ASCII.GetBytes(RigctldProtocol.Format(request, result));
                await stream.WriteAsync(response, token).ConfigureAwait(false);
                if (result.CloseConnection) break;
            }
        }
    }

    private static async ValueTask<string?> ReadLineAsync(Stream stream, int maximumLength, CancellationToken token)
    {
        var buffer = new byte[1];
        var result = new StringBuilder(Math.Min(maximumLength, 128));
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            if (buffer[0] == (byte)'\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= maximumLength) throw new InvalidDataException("Command is too long.");
            result.Append((char)buffer[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _clientSlots.Dispose();
        _stopping.Dispose();
    }
}
