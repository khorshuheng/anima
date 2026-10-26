namespace Anima.Automation;

/// <summary>
/// Waits for a response that satisfies a <see cref="ResponseMatcher"/>, drawing on
/// the session event log. A response already recorded before the wait started is
/// used too — a click step returns before its response arrives, so the caller is
/// usually one step late — and each response is handed out at most once, so two
/// identical writes can be confirmed separately.
/// </summary>
public sealed class BrowserResponseWaiter
{
    /// <summary>Keeps the consumed set bounded; older ids can no longer be replayed anyway.</summary>
    private const int ConsumedCapacity = 512;

    private readonly BrowserEventLog _log;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly Queue<string> _consumedOrder = new();
    private readonly object _gate = new();

    public BrowserResponseWaiter(BrowserEventLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public async Task<BrowserEvent> WaitAsync(
        ResponseMatcher matcher,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "the response timeout must be greater than zero");
        }

        // Captured before the scan: anything recorded from here on is either in the
        // buffer below or replayed by the subscription, so the write cannot slip
        // through the gap between "look back" and "start listening".
        var cursor = _log.LatestSequence;

        if (TryTakeRecorded(matcher) is { } recorded)
        {
            return recorded;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await foreach (var item in _log.SubscribeAsync(cursor, deadline.Token).ConfigureAwait(false))
            {
                if (matcher.Matches(item) && TryConsume(item))
                {
                    return item;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response matching [{matcher}] arrived within {timeout.TotalSeconds:0.#}s.");
        }

        throw new InvalidOperationException(
            $"The session event log ended before a response matching [{matcher}] arrived.");
    }

    private BrowserEvent? TryTakeRecorded(ResponseMatcher matcher)
    {
        // Newest match wins: if a session already saw several, the most recent one
        // is the one belonging to the action that just ran.
        var recorded = _log.Read(0, int.MaxValue);

        for (var index = recorded.Count - 1; index >= 0; index--)
        {
            if (matcher.Matches(recorded[index]) && TryConsume(recorded[index]))
            {
                return recorded[index];
            }
        }

        return null;
    }

    /// <summary>
    /// Marks a response as handed out. Keyed by the request id so the same response
    /// cannot satisfy two waits, including when it is replayed from the buffer.
    /// </summary>
    private bool TryConsume(BrowserEvent item)
    {
        var key = item.RequestId ?? $"seq:{item.Sequence}";

        lock (_gate)
        {
            if (!_consumed.Add(key))
            {
                return false;
            }

            _consumedOrder.Enqueue(key);
            while (_consumedOrder.Count > ConsumedCapacity)
            {
                _consumed.Remove(_consumedOrder.Dequeue());
            }

            return true;
        }
    }
}
