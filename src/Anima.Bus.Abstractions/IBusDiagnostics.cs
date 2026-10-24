namespace Anima.Bus;

/// <summary>Process-local view of bus traffic; counters reset when the host restarts.</summary>
public interface IBusDiagnostics
{
    BusSnapshot GetSnapshot();
}

public sealed record BusSnapshot(
    IReadOnlyList<BusTopicSnapshot> Topics,
    IReadOnlyList<BusSubscriptionSnapshot> Subscriptions,
    long TotalPublished,
    long TotalDelivered,
    long TotalDropped,
    long TotalFailed);

public sealed record BusTopicSnapshot(
    string Topic,
    long PublishedCount,
    DateTimeOffset? LastTimestamp,
    IReadOnlyList<string> TypeNames,
    IReadOnlyList<string> Senders);

public sealed record BusSubscriptionSnapshot(
    string Id,
    string Name,
    string Pattern,
    long Delivered,
    long Dropped,
    long Failed,
    DateTimeOffset? LastActivity);
