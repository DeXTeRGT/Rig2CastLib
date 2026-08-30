using System.Threading.Channels;

namespace Rig2Cast.Runtime.Scheduling;

public sealed class RadioCommandScheduler : IAsyncDisposable
{
    private readonly Channel<IWorkItem> _safety = Channel.CreateUnbounded<IWorkItem>();
    private readonly Channel<IWorkItem> _normal = Channel.CreateUnbounded<IWorkItem>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _processor;
    private int _disposed;

    public RadioCommandScheduler() => _processor = ProcessAsync();

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
        return await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (_safety.Reader.TryRead(out IWorkItem? safetyItem))
                {
                    await safetyItem.RunAsync(_stopping.Token).ConfigureAwait(false);
                    continue;
                }

                if (_normal.Reader.TryRead(out IWorkItem? normalItem))
                {
                    await normalItem.RunAsync(_stopping.Token).ConfigureAwait(false);
                    continue;
                }

                Task<bool> safetyReady = _safety.Reader.WaitToReadAsync(_stopping.Token).AsTask();
                Task<bool> normalReady = _normal.Reader.WaitToReadAsync(_stopping.Token).AsTask();
                await Task.WhenAny(safetyReady, normalReady).ConfigureAwait(false);
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
    }

    private interface IWorkItem
    {
        ValueTask RunAsync(CancellationToken schedulerToken);

        void Cancel(CancellationToken cancellationToken);
    }

    private sealed class WorkItem<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken callerToken) : IWorkItem
    {
        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask RunAsync(CancellationToken schedulerToken)
        {
            if (callerToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(callerToken);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, schedulerToken);
            try
            {
                Completion.TrySetResult(await operation(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Completion.TrySetCanceled(linked.Token);
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
