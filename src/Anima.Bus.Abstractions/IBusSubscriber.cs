namespace Anima.Bus;

public sealed class SubscriptionOptions
{
    /// <summary>Queue capacity for this subscription. Defaults to <see cref="BusOptions.SubscriptionCapacity"/>.</summary>
    public int? Capacity { get; init; }

    /// <summary>Human-readable owner, useful in logs and dead-letter headers.</summary>
    public string? Name { get; init; }
}

public interface IBusSubscriber
{
    /// <summary>
    /// Subscribe to a topic pattern. <c>*</c> matches one segment and <c>**</c>
    /// matches the remainder. Dispose the returned handle to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(
        string topicPattern,
        Func<MessageEnvelope, CancellationToken, Task> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default);
}
