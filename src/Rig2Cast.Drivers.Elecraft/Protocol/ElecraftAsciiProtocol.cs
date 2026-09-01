using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Elecraft.Protocol;

public sealed class ElecraftAsciiProtocol : IAsyncDisposable
{
    private const int MaximumFrameLength = 512;
    private static readonly Encoding Ascii = Encoding.ASCII;
    private readonly IRadioTransport _transport;
    private readonly TimeSpan _responseTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly Channel<string> _unsolicited;
    private readonly Task _reader;
    private PendingQuery? _pending;
    private Exception? _terminalFailure;
    private int _droppedUnsolicited;
    private int _disposed;

    public ElecraftAsciiProtocol(IRadioTransport transport, TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
            throw new InvalidOperationException("The transport must be connected before starting the Elecraft protocol session.");
        _transport = transport;
        _unsolicited = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            _ => Interlocked.Increment(ref _droppedUnsolicited));
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(2);
        if (_responseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        _reader = ReadLoopAsync();
    }

    public async ValueTask SendAsync(string command, CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFrameAsync(command, _stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public async ValueTask<string> QueryAsync(
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken = default) =>
        await QueryAsync(command, expectedPrefix, _ => true, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> QueryAsync(
        string command,
        string expectedPrefix,
        Func<string, bool> responseValidator,
        CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        ValidatePrefix(expectedPrefix);
        ArgumentNullException.ThrowIfNull(responseValidator);
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new PendingQuery(Frame(command), expectedPrefix, responseValidator);
        try
        {
            lock (_pendingGate)
            {
                if (_pending is not null)
                    throw new InvalidOperationException("Only one Elecraft query may await a response at a time.");
                _pending = pending;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await WriteFrameAsync(command, _stopping.Token).ConfigureAwait(false);
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                    pending.IsArmed = true;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_responseTimeout);
            try
            {
                return await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var timeoutException = new TimeoutException(
                    $"No matching Elecraft CAT response was received within {_responseTimeout}.", exception);
                FailSession(new RadioConnectionException(
                    "The Elecraft CAT session is unusable after a response timeout.", timeoutException));
                throw timeoutException;
            }
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }
            _transactionGate.Release();
        }
    }

    public async IAsyncEnumerable<string> WatchUnsolicitedFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (string frame in _unsolicited.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    public int ConsumeDroppedUnsolicitedFrameCount() =>
        Interlocked.Exchange(ref _droppedUnsolicited, 0);

    public int DroppedUnsolicitedFrameCount => Volatile.Read(ref _droppedUnsolicited);

    public static string Frame(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        string value = command.EndsWith(';') ? command : $"{command};";
        foreach (char character in value)
        {
            if (character > 0x7f || (char.IsControl(character) && character != ' '))
                throw new ArgumentException("Elecraft CAT commands must contain printable ASCII only.", nameof(command));
        }
        if (value[..^1].Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("A CAT command may contain only its final semicolon terminator.", nameof(command));
        return value.ToUpperInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        FailPending(new ObjectDisposedException(nameof(ElecraftAsciiProtocol)));
        _unsolicited.Writer.TryComplete();
        _transactionGate.Dispose();
        _stopping.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[256];
        byte[] frame = new byte[MaximumFrameLength];
        int frameLength = 0;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int count = await _transport.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (count == 0)
                    throw new RadioConnectionException("The Elecraft radio closed the connection.");
                for (int index = 0; index < count; index++)
                {
                    byte value = buffer[index];
                    if (value > 0x7f || value < 0x20)
                    {
                        frameLength = 0;
                        FailPending(new ElecraftProtocolException("A CAT frame contained a non-printable ASCII byte."));
                        continue;
                    }
                    if (frameLength == MaximumFrameLength)
                    {
                        frameLength = 0;
                        FailPending(new ElecraftProtocolException($"A CAT frame exceeded {MaximumFrameLength} bytes."));
                    }
                    frame[frameLength++] = value;
                    if (value == (byte)';')
                    {
                        RouteFrame(Ascii.GetString(frame, 0, frameLength));
                        frameLength = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailSession(exception is RadioConnectionException
                ? exception
                : new RadioConnectionException("The Elecraft CAT transport read failed.", exception));
        }
    }

    private async ValueTask WriteFrameAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.WriteAsync(Ascii.GetBytes(Frame(command)), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RadioConnectionException("The Elecraft CAT transport write failed.", exception);
            FailSession(failure);
            throw failure;
        }
    }

    private void RouteFrame(string frame)
    {
        PendingQuery? match = null;
        Exception? rejection = null;
        lock (_pendingGate)
        {
            if (_pending is { IsArmed: true } && frame == "?;")
            {
                match = _pending;
                rejection = new ElecraftCommandRejectedException(_pending.Command);
                _pending = null;
            }
            else if (_pending is { IsArmed: true } &&
                     frame.StartsWith(_pending.ExpectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                     _pending.ResponseValidator(frame))
            {
                match = _pending;
                _pending = null;
            }
        }
        if (match is not null)
        {
            if (rejection is not null)
                match.Completion.TrySetException(rejection);
            else
                match.Completion.TrySetResult(frame);
        }
        else
            _unsolicited.Writer.TryWrite(frame);
    }

    private void FailPending(Exception exception)
    {
        PendingQuery? pending;
        lock (_pendingGate)
        {
            pending = _pending;
            _pending = null;
        }
        pending?.Completion.TrySetException(exception);
    }

    private void FailSession(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _terminalFailure, exception, null) is not null)
            return;
        FailPending(exception);
        _unsolicited.Writer.TryComplete(exception);
        _stopping.Cancel();
    }

    private void EnsureOperational()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _terminalFailure) is Exception failure)
            throw new RadioConnectionException("The Elecraft CAT session is faulted and must be replaced.", failure);
    }

    private static void ValidatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length is < 2 or > 3 || prefix.Any(character => !char.IsAsciiLetter(character) && character != '$'))
            throw new ArgumentException("An Elecraft response prefix must contain two or three command characters.", nameof(prefix));
    }

    private sealed class PendingQuery(
        string command, string expectedPrefix, Func<string, bool> responseValidator)
    {
        public string Command { get; } = command;
        public string ExpectedPrefix { get; } = expectedPrefix;
        public Func<string, bool> ResponseValidator { get; } = responseValidator;
        public bool IsArmed { get; set; }
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
