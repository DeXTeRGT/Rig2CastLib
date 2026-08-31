using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Rig2Cast.Abstractions.Events;

namespace Rig2Cast.Runtime.Events;

internal sealed class RadioEventHub
{
    private const int DefaultSubscriberCapacity = 256;
    private readonly object _gate = new();
    private readonly HashSet<Channel<RadioEvent>> _subscribers = [];
    private long _sequence;
    private bool _completed;

    public RadioEvent Publish(RadioEventKind kind, object? payload = null)
    {
        RadioEvent radioEvent = new(
            Interlocked.Increment(ref _sequence),
            kind,
            DateTimeOffset.UtcNow,
            payload);

        lock (_gate)
        {
            foreach (Channel<RadioEvent> subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(radioEvent);
            }
        }

        return radioEvent;
    }

    public async IAsyncEnumerable<RadioEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<RadioEvent>(new BoundedChannelOptions(DefaultSubscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_gate)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        try
        {
            await foreach (RadioEvent radioEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return radioEvent;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (Channel<RadioEvent> subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}
