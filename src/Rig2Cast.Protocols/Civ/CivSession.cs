using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Protocols.Civ;

/// <summary>
/// Owns serialized CI-V transactions and a continuous addressed-frame read loop.
/// Command payload interpretation remains the responsibility of model drivers.
/// </summary>
public sealed class CivSession : IAsyncDisposable
{
    public const byte NegativeAcknowledgement = 0xFA;
    public const byte Acknowledgement = 0xFB;

    private readonly IRadioTransport _transport;
    private readonly CivSessionOptions _options;
    private readonly TimeSpan _responseTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly Channel<CivFrame> _unsolicited;
    private readonly Task _reader;
    private PendingQuery? _pending;
    private CivFrame? _lastOutbound;
    private Exception? _terminalFailure;
    private int _droppedUnsolicited;
    private int _disposed;

    public CivSession(
        IRadioTransport transport,
        CivSessionOptions? options = null,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
            throw new InvalidOperationException("The transport must be connected before starting a CI-V session.");

        _options = options ?? new CivSessionOptions();
        if (_options.MaximumFrameLength < CivFrameDecoder.MinimumFrameLength ||
            _options.ReadBufferLength <= 0 ||
            _options.UnsolicitedCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CI-V session buffer sizes are invalid.");
        }

        _responseTimeout = responseTimeout ?? _options.DefaultResponseTimeout;
        if (_responseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));

        _transport = transport;
        _unsolicited = Channel.CreateBounded<CivFrame>(
            new BoundedChannelOptions(_options.UnsolicitedCapacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            _ => Interlocked.Increment(ref _droppedUnsolicited));
        _reader = ReadLoopAsync();
    }

    public int DroppedUnsolicitedFrameCount => Volatile.Read(ref _droppedUnsolicited);

    public int ConsumeDroppedUnsolicitedFrameCount() =>
        Interlocked.Exchange(ref _droppedUnsolicited, 0);

    public async ValueTask SendAsync(CivFrame command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
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

    public ValueTask<CivFrame> CommandExpectingAcknowledgementAsync(
        CivFrame command,
        CancellationToken cancellationToken = default) =>
        QueryCoreAsync(command, ReadOnlyMemory<byte>.Empty, true, static _ => true, cancellationToken);

    public ValueTask<CivFrame> QueryAsync(
        CivFrame command,
        ReadOnlyMemory<byte> expectedMessagePrefix,
        CancellationToken cancellationToken = default) =>
        QueryAsync(command, expectedMessagePrefix, static _ => true, cancellationToken);

    public ValueTask<CivFrame> QueryAsync(
        CivFrame command,
        ReadOnlyMemory<byte> expectedMessagePrefix,
        Func<CivFrame, bool> responseValidator,
        CancellationToken cancellationToken = default)
    {
        if (expectedMessagePrefix.IsEmpty)
            throw new ArgumentException("A CI-V query response prefix cannot be empty.", nameof(expectedMessagePrefix));
        return QueryCoreAsync(command, expectedMessagePrefix, false, responseValidator, cancellationToken);
    }

    public async IAsyncEnumerable<CivFrame> WatchUnsolicitedFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (CivFrame frame in _unsolicited.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
        FailPending(new ObjectDisposedException(nameof(CivSession)));
        _unsolicited.Writer.TryComplete();
        _transactionGate.Dispose();
        _stopping.Dispose();
    }

    private async ValueTask<CivFrame> QueryCoreAsync(
        CivFrame command,
        ReadOnlyMemory<byte> expectedMessagePrefix,
        bool acceptsAcknowledgement,
        Func<CivFrame, bool> responseValidator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(responseValidator);
        EnsureOperational();
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new PendingQuery(
            command,
            expectedMessagePrefix.ToArray(),
            acceptsAcknowledgement,
            responseValidator);
        bool commandCommitted = false;
        try
        {
            lock (_pendingGate)
            {
                if (_pending is not null)
                    throw new InvalidOperationException("Only one CI-V query may await a response at a time.");
                _pending = pending;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await WriteFrameAsync(command, _stopping.Token).ConfigureAwait(false);
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
                    $"No matching CI-V response was received within {_responseTimeout}.", exception);
                FailSession(new RadioConnectionException(
                    "The CI-V session is unusable after a response timeout.", timeoutException));
                throw timeoutException;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && commandCommitted)
            {
                FailSession(new RadioConnectionException(
                    "The CI-V session is unusable after a committed query was abandoned."));
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

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[_options.ReadBufferLength];
        var decoder = new CivFrameDecoder(_options.MaximumFrameLength);
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int count = await _transport.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (count == 0)
                    throw new RadioConnectionException("The CI-V radio closed the connection.");
                foreach (CivFrame frame in decoder.Append(buffer.AsSpan(0, count)))
                    RouteFrame(frame);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailSession(exception is RadioConnectionException
                ? exception
                : new RadioConnectionException("The CI-V transport read failed.", exception));
        }
    }

    private async ValueTask WriteFrameAsync(CivFrame frame, CancellationToken cancellationToken)
    {
        lock (_pendingGate)
            _lastOutbound = frame;
        try
        {
            await _transport.WriteAsync(CivFrameCodec.Encode(frame), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _terminalFailure) is Exception terminalFailure)
            {
                throw new RadioConnectionException(
                    "The CI-V transport write was interrupted because the session failed.",
                    terminalFailure);
            }
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RadioConnectionException("The CI-V transport write failed.", exception);
            FailSession(failure);
            throw failure;
        }
    }

    private void RouteFrame(CivFrame frame)
    {
        PendingQuery? match = null;
        bool rejected = false;
        lock (_pendingGate)
        {
            if (_lastOutbound is not null && FramesEqual(frame, _lastOutbound))
                return;

            if (_pending is { IsArmed: true } pending && HasResponseAddresses(frame, pending.Command))
            {
                if (IsSingleByteMessage(frame, NegativeAcknowledgement))
                {
                    rejected = true;
                    match = pending;
                }
                else if ((pending.AcceptsAcknowledgement && IsSingleByteMessage(frame, Acknowledgement)) ||
                         (!pending.AcceptsAcknowledgement &&
                          frame.Message.Span.StartsWith(pending.ExpectedMessagePrefix) &&
                          pending.ResponseValidator(frame)))
                {
                    match = pending;
                }

                if (match is not null)
                    _pending = null;
            }
        }

        if (match is not null)
        {
            if (rejected)
                match.Completion.TrySetException(new CivCommandRejectedException());
            else
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
            return;
        FailPending(exception);
        _unsolicited.Writer.TryComplete(exception);
        _stopping.Cancel();
    }

    private void EnsureOperational()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _terminalFailure) is Exception failure)
            throw new RadioConnectionException("The CI-V session is faulted and must be replaced.", failure);
    }

    private static bool HasResponseAddresses(CivFrame response, CivFrame command) =>
        response.Destination == command.Source && response.Source == command.Destination;

    private static bool IsSingleByteMessage(CivFrame frame, byte value) =>
        frame.Message.Length == 1 && frame.Message.Span[0] == value;

    private static bool FramesEqual(CivFrame left, CivFrame right) =>
        left.Destination == right.Destination &&
        left.Source == right.Source &&
        left.Message.Span.SequenceEqual(right.Message.Span);

    private sealed class PendingQuery(
        CivFrame command,
        byte[] expectedMessagePrefix,
        bool acceptsAcknowledgement,
        Func<CivFrame, bool> responseValidator)
    {
        public CivFrame Command { get; } = command;
        public byte[] ExpectedMessagePrefix { get; } = expectedMessagePrefix;
        public bool AcceptsAcknowledgement { get; } = acceptsAcknowledgement;
        public Func<CivFrame, bool> ResponseValidator { get; } = responseValidator;
        public bool IsArmed { get; set; }
        public TaskCompletionSource<CivFrame> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
