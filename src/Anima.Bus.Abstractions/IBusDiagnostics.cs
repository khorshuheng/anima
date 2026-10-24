namespace Anima.Bus;

/// <summary>Process-local view of bus traffic; counters reset when the host restarts.</summary>
public interface IBusDiagnostics
{
    BusSnapshot GetSnapshot();
}

/// <summary>
/// Bounded diagnostics snapshot. <see cref="Topics"/> holds at most
/// <see cref="BusOptions.MaxSnapshotTopics"/> topics, busiest first, of the
/// <see cref="TrackedTopics"/> topics currently tracked; <see cref="TopicsTruncated"/>
/// is true when tracked topics were left out.
/// </summary>
public sealed record BusSnapshot(
    IReadOnlyList<BusTopicSnapshot> Topics,
    IReadOnlyList<BusSubscriptionSnapshot> Subscriptions,
    long TotalPublished,
    long TotalDelivered,
    long TotalDropped,
    long TotalFailed,
    int TrackedTopics = 0,
    bool TopicsTruncated = false);

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
