using System.Runtime.CompilerServices;
using Rig2Cast.Abstractions.Events;

namespace Rig2Cast.Runtime.Events;

internal sealed class RadioEventHub(TimeProvider? timeProvider = null)
{
    private const int DefaultSubscriberCapacity = 256;
    private readonly object _gate = new();
    private readonly HashSet<Subscriber> _subscribers = [];
    private long _sequence;
    private bool _completed;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public RadioEvent Publish(RadioEventKind kind, object? payload = null)
    {
        RadioEvent radioEvent = new(
            Interlocked.Increment(ref _sequence),
            kind,
            _timeProvider.GetUtcNow(),
            payload);

        lock (_gate)
        {
            foreach (Subscriber subscriber in _subscribers)
                subscriber.Enqueue(radioEvent);
        }

        return radioEvent;
    }

    public async IAsyncEnumerable<RadioEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriber = new Subscriber(DefaultSubscriberCapacity);

        lock (_gate)
        {
            if (_completed)
                subscriber.Complete();
            else
                _subscribers.Add(subscriber);
        }

        try
        {
            while (await subscriber.ReadAsync(cancellationToken).ConfigureAwait(false) is SubscriberRead read)
            {
                if (read.Gap is RadioEventDeliveryGap gap)
                {
                    yield return new RadioEvent(
                        gap.FirstDroppedSequence,
                        RadioEventKind.Diagnostic,
                        _timeProvider.GetUtcNow(),
                        gap);
                }

                yield return read.Event;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
                subscriber.Complete();
            }
            subscriber.Dispose();
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (Subscriber subscriber in _subscribers)
                subscriber.Complete();

            _subscribers.Clear();
        }
    }

    private sealed class Subscriber(int capacity) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<RadioEvent> _queue = new(capacity);
        private readonly SemaphoreSlim _available = new(0, 1);
        private long _droppedCount;
        private long _firstDroppedSequence;
        private long _lastDroppedSequence;
        private bool _completed;

        public void Enqueue(RadioEvent radioEvent)
        {
            lock (_gate)
            {
                if (_completed)
                    return;

                if (_queue.Count == capacity)
                {
                    RadioEvent dropped = _queue.Dequeue();
                    if (_droppedCount++ == 0)
                        _firstDroppedSequence = dropped.Sequence;
                    _lastDroppedSequence = dropped.Sequence;
                }
                _queue.Enqueue(radioEvent);
            }

            Signal();
        }

        public async ValueTask<SubscriberRead?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        RadioEvent radioEvent = _queue.Dequeue();
                        RadioEventDeliveryGap? gap = TakeGap();
                        return new SubscriberRead(radioEvent, gap);
                    }
                    if (_completed)
                        return null;
                }

                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Complete()
        {
            lock (_gate)
                _completed = true;
            Signal();
        }

        public void Dispose() => _available.Dispose();

        private void Signal()
        {
            try
            {
                _available.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending.
            }
        }

        private RadioEventDeliveryGap? TakeGap()
        {
            if (_droppedCount == 0)
                return null;

            var gap = new RadioEventDeliveryGap(
                _droppedCount,
                _firstDroppedSequence,
                _lastDroppedSequence,
                capacity);
            _droppedCount = 0;
            _firstDroppedSequence = 0;
            _lastDroppedSequence = 0;
            return gap;
        }
    }

    private sealed record SubscriberRead(RadioEvent Event, RadioEventDeliveryGap? Gap);
}
