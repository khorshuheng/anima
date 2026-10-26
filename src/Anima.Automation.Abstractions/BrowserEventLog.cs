using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Anima.Automation;

/// <summary>
/// Bounded, per-session event buffer with fan-out to live subscribers. Polling
/// reads the buffer; streaming subscribes to it, so both surfaces observe the
/// same sequence numbers and a slow reader can never block the browser.
/// </summary>
public sealed class BrowserEventLog
{
    /// <summary>Live subscriber capacity; a subscriber that falls this far behind drops its own events.</summary>
    private const int SubscriberCapacity = 256;

    private readonly object _gate = new();
    private readonly Queue<BrowserEvent> _events = new();
    private readonly List<Channel<BrowserEvent>> _subscribers = [];
    private readonly int _capacity;

    private long _sequence;
    private long _dropped;
    private bool _completed;

    public BrowserEventLog(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Highest sequence assigned so far; 0 when nothing was recorded.</summary>
    public long LatestSequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>Events evicted because the buffer was full.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>
    /// Buffered event count. Diagnostics only; poll responses report the eviction
    /// count instead, because that is what a consumer needs to detect loss.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>
    /// Records an event, assigning its sequence and timestamp. Callers provide the
    /// payload only; sequence and time are owned by the log.
    /// </summary>
    public BrowserEvent Append(BrowserEvent draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        BrowserEvent recorded;
        Channel<BrowserEvent>[] subscribers;

        lock (_gate)
        {
            recorded = draft with
            {
                Sequence = ++_sequence,
                Timestamp = DateTimeOffset.UtcNow,
            };

            _events.Enqueue(recorded);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
                _dropped++;
            }

            subscribers = _subscribers.Count == 0 ? [] : [.. _subscribers];
        }

        // Written outside the lock: a stalled subscriber must not stall the page.
        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(recorded);
        }

        return recorded;
    }

    /// <summary>Buffered events with a sequence greater than <paramref name="afterSequence"/>.</summary>
    public IReadOnlyList<BrowserEvent> Read(long afterSequence = 0, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            return _events
                .Where(item => item.Sequence > afterSequence)
                .Take(limit)
                .ToArray();
        }
    }

    /// <summary>
    /// Stops the log: every live subscriber finishes after the events already
    /// queued, and later subscribers replay the buffer and finish immediately.
    /// Called when the session closes so streams end instead of hanging silently.
    /// </summary>
    public void Complete()
    {
        Channel<BrowserEvent>[] subscribers;

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Replays buffered events after <paramref name="afterSequence"/>, then streams
    /// new ones. Backlog and live events are joined under the same lock, so nothing
    /// recorded in between is lost.
    /// </summary>
    public async IAsyncEnumerable<BrowserEvent> SubscribeAsync(
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<BrowserEvent>? channel;
        BrowserEvent[] backlog;
        long lastSeen;

        lock (_gate)
        {
            // A cursor past the newest sequence is stale (another session, a
            // persisted cursor, or a restart, since every session starts at 1).
            // Clamping it keeps the stream alive instead of silently swallowing
            // every future event with Sequence <= cursor.
            lastSeen = Math.Min(afterSequence, _sequence);
            backlog = _events.Where(item => item.Sequence > lastSeen).ToArray();

            if (_completed)
            {
                channel = null;
            }
            else
            {
                channel = Channel.CreateBounded<BrowserEvent>(new BoundedChannelOptions(SubscriberCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
                _subscribers.Add(channel);
            }
        }

        try
        {
            foreach (var item in backlog)
            {
                yield return item;
            }

            if (channel is null)
            {
                // Already completed: replay only, so callers never wait forever.
                yield break;
            }

            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.Sequence <= lastSeen)
                {
                    continue;
                }

                lastSeen = item.Sequence;
                yield return item;
            }
        }
        finally
        {
            if (channel is not null)
            {
                lock (_gate)
                {
                    _subscribers.Remove(channel);
                }

                channel.Writer.TryComplete();
            }
        }
    }
}
