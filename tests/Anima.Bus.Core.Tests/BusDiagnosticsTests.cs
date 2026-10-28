using Anima.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Bus.Core.Tests;

public sealed class BusDiagnosticsTests
{
    [Fact]
    public async Task Snapshot_tracks_topic_publishes_types_and_senders()
    {
        await using var bus = CreateBus();

        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));
        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Pong", "bob"));

        var snapshot = bus.GetSnapshot();

        var topic = Assert.Single(snapshot.Topics);
        Assert.Equal("anima.test.topic", topic.Topic);
        Assert.Equal(2, topic.PublishedCount);
        Assert.NotNull(topic.LastTimestamp);
        Assert.Equal(["anima.test.v1.Ping", "anima.test.v1.Pong"], topic.TypeNames);
        Assert.Equal(["alice", "bob"], topic.Senders);
        Assert.Equal(2, snapshot.TotalPublished);
    }

    [Fact]
    public async Task Snapshot_tracks_subscription_deliveries()
    {
        await using var bus = CreateBus();
        int delivered = 0;
        await using var subscription = await bus.SubscribeAsync(
            "anima.test.topic",
            (_, _) =>
            {
                Interlocked.Increment(ref delivered);
                return Task.CompletedTask;
            },
            new SubscriptionOptions { Name = "test-subscriber" });

        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));
        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));

        var snapshot = await WaitForSnapshotAsync(bus, s => s.TotalDelivered == 2);

        var sub = Assert.Single(snapshot.Subscriptions);
        Assert.Equal("test-subscriber", sub.Name);
        Assert.Equal("anima.test.topic", sub.Pattern);
        Assert.Equal(2, sub.Delivered);
        Assert.Equal(0, sub.Dropped);
        Assert.Equal(0, sub.Failed);
        Assert.NotNull(sub.LastActivity);
        Assert.Equal(2, snapshot.TotalDelivered);
    }

    [Fact]
    public async Task Snapshot_tracks_dropped_messages()
    {
        await using var bus = CreateBus(new BusOptions
        {
            SubscriptionCapacity = 1,
            FullMode = BusFullMode.DropNewest,
            RetryDelay = TimeSpan.Zero,
        });

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await bus.SubscribeAsync(
            "anima.test.topic",
            async (_, _) =>
            {
                entered.TrySetResult();
                await gate.Task;
            });

        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // One message is buffered while the handler is blocked; the next one cannot be queued.
        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));
        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));

        var snapshot = await WaitForSnapshotAsync(bus, s => s.TotalDropped == 1);

        var sub = Assert.Single(snapshot.Subscriptions);
        Assert.Equal(1, sub.Dropped);
        Assert.NotNull(sub.LastActivity);
        Assert.Equal(1, snapshot.TotalDropped);
        Assert.Equal(3, snapshot.TotalPublished);

        gate.SetResult();
    }

    [Fact]
    public async Task Snapshot_tracks_failed_deliveries()
    {
        await using var bus = CreateBus(new BusOptions
        {
            MaxDeliveryAttempts = 1,
            RetryDelay = TimeSpan.Zero,
            EnableDeadLetter = false,
        });

        await using var subscription = await bus.SubscribeAsync(
            "anima.test.topic",
            (_, _) => throw new InvalidOperationException("boom"));

        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));

        var snapshot = await WaitForSnapshotAsync(bus, s => s.TotalFailed == 1);

        var sub = Assert.Single(snapshot.Subscriptions);
        Assert.Equal(0, sub.Delivered);
        Assert.Equal(0, sub.Dropped);
        Assert.Equal(1, sub.Failed);
        Assert.NotNull(sub.LastActivity);
        Assert.Equal(1, snapshot.TotalFailed);
    }

    [Fact]
    public async Task Snapshot_totals_survive_unsubscribe()
    {
        await using var bus = CreateBus();
        var subscription = await bus.SubscribeAsync(
            "anima.test.topic",
            (_, _) => Task.CompletedTask);

        await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", "alice"));
        _ = await WaitForSnapshotAsync(bus, s => s.TotalDelivered == 1);

        await subscription.DisposeAsync();

        var snapshot = bus.GetSnapshot();
        Assert.Empty(snapshot.Subscriptions);
        Assert.Equal(1, snapshot.TotalPublished);
        Assert.Equal(1, snapshot.TotalDelivered);
    }

    [Fact]
    public async Task Snapshot_reads_are_safe_during_concurrent_publishing()
    {
        await using var bus = CreateBus();
        await using var subscription = await bus.SubscribeAsync(
            "anima.test.topic",
            (_, _) => Task.CompletedTask);

        var publishers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                await bus.PublishAsync(CreateEnvelope("anima.test.topic", "anima.test.v1.Ping", $"worker-{worker}"));
            }
        }));

        var readers = Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                _ = bus.GetSnapshot();
                await Task.Yield();
            }
        }));

        await Task.WhenAll(publishers.Concat(readers));

        var snapshot = await WaitForSnapshotAsync(bus, s => s.TotalDelivered == 400);
        Assert.Equal(400, snapshot.TotalPublished);
        var topic = Assert.Single(snapshot.Topics);
        Assert.Equal(4, topic.Senders.Count);
    }

    [Fact]
    public async Task Snapshot_stops_tracking_topics_beyond_the_tracked_topic_cap()
    {
        await using var bus = CreateBus(new BusOptions { MaxTrackedTopics = 2, RetryDelay = TimeSpan.Zero });

        for (int index = 0; index < 5; index++)
        {
            await bus.PublishAsync(CreateEnvelope($"anima.test.topic{index}", "anima.test.v1.Ping", "alice"));
        }

        var capped = bus.GetSnapshot();

        Assert.Equal(5, capped.TotalPublished);
        Assert.Equal(2, capped.TrackedTopics);
        Assert.Equal(["anima.test.topic0", "anima.test.topic1"], capped.Topics.Select(topic => topic.Topic));
        Assert.False(capped.TopicsTruncated);

        // Topics already tracked keep counting; new ones only reach the totals.
        await bus.PublishAsync(CreateEnvelope("anima.test.topic0", "anima.test.v1.Ping", "alice"));
        await bus.PublishAsync(CreateEnvelope("anima.test.topic9", "anima.test.v1.Ping", "alice"));

        var snapshot = bus.GetSnapshot();
        Assert.Equal(7, snapshot.TotalPublished);
        Assert.Equal(2, snapshot.TrackedTopics);
        Assert.Equal(2, snapshot.Topics.Single(topic => topic.Topic == "anima.test.topic0").PublishedCount);
    }

    [Fact]
    public async Task Snapshot_caps_type_names_and_senders_per_topic()
    {
        await using var bus = CreateBus(new BusOptions
        {
            MaxTypesPerTopic = 2,
            MaxSendersPerTopic = 3,
            RetryDelay = TimeSpan.Zero,
        });

        for (int index = 0; index < 5; index++)
        {
            await bus.PublishAsync(CreateEnvelope(
                "anima.test.topic",
                $"anima.test.v1.Type{index}",
                $"sender-{index}"));
        }

        var topic = Assert.Single(bus.GetSnapshot().Topics);

        Assert.Equal(5, topic.PublishedCount);
        Assert.Equal(["anima.test.v1.Type0", "anima.test.v1.Type1"], topic.TypeNames);
        Assert.Equal(["sender-0", "sender-1", "sender-2"], topic.Senders);
    }

    [Fact]
    public async Task Snapshot_returns_the_busiest_topics_and_flags_the_truncation()
    {
        await using var bus = CreateBus(new BusOptions { MaxSnapshotTopics = 2, RetryDelay = TimeSpan.Zero });

        await bus.PublishAsync(CreateEnvelope("anima.test.quiet", "anima.test.v1.Ping", "alice"));
        for (int index = 0; index < 3; index++)
        {
            await bus.PublishAsync(CreateEnvelope("anima.test.busy", "anima.test.v1.Ping", "alice"));
        }

        await bus.PublishAsync(CreateEnvelope("anima.test.steady", "anima.test.v1.Ping", "alice"));
        await bus.PublishAsync(CreateEnvelope("anima.test.steady", "anima.test.v1.Ping", "alice"));

        var snapshot = bus.GetSnapshot();

        Assert.Equal(3, snapshot.TrackedTopics);
        Assert.True(snapshot.TopicsTruncated);
        Assert.Equal(["anima.test.busy", "anima.test.steady"], snapshot.Topics.Select(topic => topic.Topic));
    }

    private static InProcMessageBus CreateBus(BusOptions? options = null) => new(
        Options.Create(options ?? new BusOptions { RetryDelay = TimeSpan.Zero }),
        NullLogger<InProcMessageBus>.Instance);

    private static MessageEnvelope CreateEnvelope(string topic, string typeName, string sender) => MessageEnvelope.Create(
        topic,
        typeName,
        new byte[] { 1, 2, 3 },
        ContractContentTypes.ProtobufJson,
        sender: sender);

    private static async Task<BusSnapshot> WaitForSnapshotAsync(
        InProcMessageBus bus,
        Func<BusSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = bus.GetSnapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        var latest = bus.GetSnapshot();
        throw new TimeoutException(
            $"Snapshot condition was not reached. published={latest.TotalPublished} " +
            $"delivered={latest.TotalDelivered} dropped={latest.TotalDropped} failed={latest.TotalFailed}");
    }
}
