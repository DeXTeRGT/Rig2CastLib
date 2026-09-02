using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Protocols.Ascii;

public sealed class AsciiCatSession : IAsyncDisposable
{
    private static readonly Encoding Ascii = Encoding.ASCII;
    private readonly IRadioTransport _transport;
    private readonly AsciiCatSessionOptions _options;
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

    public AsciiCatSession(
        IRadioTransport transport,
        AsciiCatSessionOptions options,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        if (!transport.IsConnected)
            throw new InvalidOperationException(
                $"The transport must be connected before starting the {options.ProtocolName} protocol session.");
        if (options.MaximumFrameLength <= 0 || options.ReadBufferLength <= 0 || options.UnsolicitedCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "ASCII session buffer sizes must be positive.");

        _transport = transport;
        _options = options;
        _responseTimeout = responseTimeout ?? options.DefaultResponseTimeout;
        if (_responseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        _unsolicited = Channel.CreateBounded<string>(
            new BoundedChannelOptions(options.UnsolicitedCapacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            _ => Interlocked.Increment(ref _droppedUnsolicited));
        _reader = ReadLoopAsync();
    }

    public int DroppedUnsolicitedFrameCount => Volatile.Read(ref _droppedUnsolicited);

    public int ConsumeDroppedUnsolicitedFrameCount() => Interlocked.Exchange(ref _droppedUnsolicited, 0);

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

    public ValueTask<string> QueryAsync(
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken = default) =>
        QueryAsync(command, expectedPrefix, static _ => true, cancellationToken);

    public async ValueTask<string> QueryAsync(
        string command,
        string expectedPrefix,
        Func<string, bool> responseValidator,
        CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        _options.ValidateResponsePrefix(expectedPrefix);
        ArgumentNullException.ThrowIfNull(responseValidator);
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new PendingQuery(_options.FrameCommand(command), expectedPrefix, responseValidator);
        bool commandCommitted = false;
        try
        {
            lock (_pendingGate)
            {
                if (_pending is not null)
                    throw new InvalidOperationException(
                        $"Only one {_options.ProtocolName} query may await a response at a time.");
                _pending = pending;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await WriteFramedAsync(pending.Command, _stopping.Token).ConfigureAwait(false);
            commandCommitted = true;
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
                    $"No matching {_options.ProtocolName} CAT response was received within {_responseTimeout}.", exception);
                FailSession(new RadioConnectionException(
                    $"The {_options.ProtocolName} CAT session is unusable after a response timeout.", timeoutException));
                throw timeoutException;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && commandCommitted)
            {
                FailSession(new RadioConnectionException(
                    $"The {_options.ProtocolName} CAT session is unusable after a committed query was abandoned."));
                throw;
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
        FailPending(new ObjectDisposedException(nameof(AsciiCatSession)));
        _unsolicited.Writer.TryComplete();
        _transactionGate.Dispose();
        _stopping.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[_options.ReadBufferLength];
        byte[] frame = new byte[_options.MaximumFrameLength];
        int frameLength = 0;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int count = await _transport.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (count == 0)
                    throw new RadioConnectionException($"The {_options.ProtocolName} radio closed the connection.");
                for (int index = 0; index < count; index++)
                {
                    byte value = buffer[index];
                    if (value > 0x7f || value < 0x20)
                    {
                        frameLength = 0;
                        FailPending(_options.InvalidFrameException("A CAT frame contained a non-printable ASCII byte."));
                        continue;
                    }
                    if (frameLength == _options.MaximumFrameLength)
                    {
                        frameLength = 0;
                        FailPending(_options.InvalidFrameException(
                            $"A CAT frame exceeded {_options.MaximumFrameLength} bytes."));
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
                : new RadioConnectionException($"The {_options.ProtocolName} CAT transport read failed.", exception));
        }
    }

    private ValueTask WriteFrameAsync(string command, CancellationToken cancellationToken) =>
        WriteFramedAsync(_options.FrameCommand(command), cancellationToken);

    private async ValueTask WriteFramedAsync(string framedCommand, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.WriteAsync(Ascii.GetBytes(framedCommand), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _terminalFailure) is Exception terminalFailure)
            {
                throw new RadioConnectionException(
                    $"The {_options.ProtocolName} CAT transport write was interrupted because the session failed.",
                    terminalFailure);
            }
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RadioConnectionException(
                $"The {_options.ProtocolName} CAT transport write failed.", exception);
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
            if (_pending is { IsArmed: true } pending)
            {
                rejection = _options.CommandRejection(frame, pending.Command);
                if (rejection is not null ||
                    (frame.StartsWith(pending.ExpectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                     pending.ResponseValidator(frame)))
                {
                    match = pending;
                    _pending = null;
                }
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
            throw new RadioConnectionException(
                $"The {_options.ProtocolName} CAT session is faulted and must be replaced.", failure);
    }

    private sealed class PendingQuery(
        string command,
        string expectedPrefix,
        Func<string, bool> responseValidator)
    {
        public string Command { get; } = command;
        public string ExpectedPrefix { get; } = expectedPrefix;
        public Func<string, bool> ResponseValidator { get; } = responseValidator;
        public bool IsArmed { get; set; }
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
