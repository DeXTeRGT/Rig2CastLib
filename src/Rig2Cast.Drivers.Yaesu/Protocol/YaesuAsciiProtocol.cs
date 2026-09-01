using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Yaesu.Protocol;

public sealed class YaesuAsciiProtocol : IAsyncDisposable
{
    private const int MaximumResponseLength = 512;
    private const int ReadBufferLength = 256;
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

    public YaesuAsciiProtocol(IRadioTransport transport, TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
        {
            throw new InvalidOperationException("The transport must be connected before starting the Yaesu protocol session.");
        }

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
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

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
        string expectedResponsePrefix,
        CancellationToken cancellationToken = default) =>
        await QueryAsync(command, expectedResponsePrefix, _ => true, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> QueryAsync(
        string command,
        string expectedResponsePrefix,
        Func<string, bool> responseValidator,
        CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        ValidatePrefix(expectedResponsePrefix);
        ArgumentNullException.ThrowIfNull(responseValidator);
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new PendingQuery(expectedResponsePrefix, responseValidator);
        try
        {
            lock (_pendingGate)
            {
                if (_pending is not null)
                {
                    throw new InvalidOperationException("Only one Yaesu query may await a response at a time.");
                }

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
                    $"No matching CAT response was received within {_responseTimeout}.", exception);
                var connectionException = new RadioConnectionException(
                    "The CAT protocol session is unusable after a response timeout.", timeoutException);
                FailSession(connectionException);
                throw timeoutException;
            }
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }

            _transactionGate.Release();
        }
    }

    public async IAsyncEnumerable<string> WatchUnsolicitedFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (string frame in _unsolicited.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
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
            if (character > 0x7f || char.IsControl(character))
            {
                throw new ArgumentException("Yaesu CAT commands must contain printable ASCII characters only.", nameof(command));
            }
        }

        if (value[..^1].Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("A CAT command may contain only its final semicolon terminator.", nameof(command));
        }

        return value.ToUpperInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }

        FailPending(new ObjectDisposedException(nameof(YaesuAsciiProtocol)));
        _unsolicited.Writer.TryComplete();
        _transactionGate.Dispose();
        _stopping.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[ReadBufferLength];
        byte[] frame = new byte[MaximumResponseLength];
        int frameLength = 0;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int count = await _transport.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new RadioConnectionException("The radio closed the connection.");
                }

                for (int index = 0; index < count; index++)
                {
                    byte value = buffer[index];
                    if (value > 0x7f || value < 0x20)
                    {
                        frameLength = 0;
                        FailPending(new YaesuProtocolException("A CAT frame contained a non-printable ASCII byte."));
                        continue;
                    }

                    if (frameLength == MaximumResponseLength)
                    {
                        frameLength = 0;
                        FailPending(new YaesuProtocolException(
                            $"A CAT frame exceeded {MaximumResponseLength} bytes."));
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
        catch (OperationCanceledException exception)
        {
            FailSession(new RadioConnectionException("The CAT transport read was interrupted.", exception));
        }
        catch (Exception exception)
        {
            FailSession(exception is RadioConnectionException
                ? exception
                : new RadioConnectionException("The CAT transport read failed.", exception));
        }
    }

    private async ValueTask WriteFrameAsync(string command, CancellationToken cancellationToken)
    {
        string framed = Frame(command);
        try
        {
            await _transport.WriteAsync(Ascii.GetBytes(framed), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var connectionException = new RadioConnectionException("The CAT transport write failed.", exception);
            FailSession(connectionException);
            throw connectionException;
        }
    }

    private void RouteFrame(string frame)
    {
        PendingQuery? match = null;
        lock (_pendingGate)
        {
            if (_pending is { IsArmed: true } &&
                frame.StartsWith(_pending.ExpectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                _pending.ResponseValidator(frame))
            {
                match = _pending;
                _pending = null;
            }
        }

        if (match is not null)
        {
            match.Completion.TrySetResult(frame);
        }
        else
        {
            _unsolicited.Writer.TryWrite(frame);
        }
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
        {
            return;
        }

        FailPending(exception);
        _unsolicited.Writer.TryComplete(exception);
        _stopping.Cancel();
    }

    private void EnsureOperational()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _terminalFailure) is Exception failure)
        {
            throw new RadioConnectionException(
                "The Yaesu protocol session is faulted and must be reconnected before issuing more commands.",
                failure);
        }
    }

    private static void ValidatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length != 2 || prefix.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new ArgumentException("A Yaesu response prefix must contain exactly two ASCII letters.", nameof(prefix));
        }
    }

    private sealed class PendingQuery(string expectedPrefix, Func<string, bool> responseValidator)
    {
        public string ExpectedPrefix { get; } = expectedPrefix;
        public Func<string, bool> ResponseValidator { get; } = responseValidator;
        public bool IsArmed { get; set; }

        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
