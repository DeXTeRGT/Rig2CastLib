using System.Threading.Channels;

namespace Rig2Cast.Runtime.Scheduling;

public sealed class RadioCommandScheduler : IAsyncDisposable
{
    private readonly Channel<IWorkItem> _safety;
    private readonly Channel<IWorkItem> _normal;
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _operationTimeout;
    private readonly Task _processor;
    private int _disposed;

    public RadioCommandScheduler(int queueCapacity = 256, TimeSpan? operationTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        if (_operationTimeout <= TimeSpan.Zero && _operationTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        var options = new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _safety = Channel.CreateBounded<IWorkItem>(options);
        _normal = Channel.CreateBounded<IWorkItem>(options);
        _processor = ProcessAsync();
    }

    public ValueTask ExecuteAsync(
        Func<CancellationToken, ValueTask> operation,
        RadioCommandPriority priority = RadioCommandPriority.Normal,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, priority, cancellationToken).AsVoid();

    public async ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        RadioCommandPriority priority = RadioCommandPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var item = new WorkItem<T>(operation, cancellationToken);
        ChannelWriter<IWorkItem> writer = priority == RadioCommandPriority.Safety
            ? _safety.Writer
            : _normal.Writer;

        await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        _available.Release();
        return await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await _available.WaitAsync(_stopping.Token).ConfigureAwait(false);

                if (_safety.Reader.TryRead(out IWorkItem? safetyItem))
                {
                    await safetyItem.RunAsync(_operationTimeout, _stopping.Token).ConfigureAwait(false);
                    continue;
                }

                if (_normal.Reader.TryRead(out IWorkItem? normalItem))
                {
                    await normalItem.RunAsync(_operationTimeout, _stopping.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            while (_safety.Reader.TryRead(out IWorkItem? item))
            {
                item.Cancel(_stopping.Token);
            }

            while (_normal.Reader.TryRead(out IWorkItem? item))
            {
                item.Cancel(_stopping.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _safety.Writer.TryComplete();
        _normal.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _processor.ConfigureAwait(false);
        _stopping.Dispose();
        _available.Dispose();
    }

    private interface IWorkItem
    {
        ValueTask RunAsync(TimeSpan operationTimeout, CancellationToken schedulerToken);

        void Cancel(CancellationToken cancellationToken);
    }

    private sealed class WorkItem<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken callerToken) : IWorkItem
    {
        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask RunAsync(TimeSpan operationTimeout, CancellationToken schedulerToken)
        {
            if (callerToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(callerToken);
                return;
            }

            using var operationStopping = CancellationTokenSource.CreateLinkedTokenSource(schedulerToken);
            if (operationTimeout != Timeout.InfiniteTimeSpan)
            {
                operationStopping.CancelAfter(operationTimeout);
            }

            try
            {
                T result = await operation(operationStopping.Token).ConfigureAwait(false);
                if (callerToken.IsCancellationRequested)
                {
                    Completion.TrySetCanceled(callerToken);
                }
                else
                {
                    Completion.TrySetResult(result);
                }
            }
            catch (OperationCanceledException exception) when (
                operationStopping.IsCancellationRequested && !schedulerToken.IsCancellationRequested)
            {
                Completion.TrySetException(new TimeoutException(
                    $"The radio operation exceeded its {operationTimeout} deadline.", exception));
            }
            catch (OperationCanceledException) when (schedulerToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(schedulerToken);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }

        public void Cancel(CancellationToken cancellationToken) => Completion.TrySetCanceled(cancellationToken);
    }
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> operation) =>
        _ = await operation.ConfigureAwait(false);
}
