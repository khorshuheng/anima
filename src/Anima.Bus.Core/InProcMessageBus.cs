using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Bus;

/// <summary>
/// In-process fan-out bus with bounded per-subscription queues, retry and a
/// dead-letter topic. Swap for a broker-backed implementation without touching
/// callers; <see cref="IMessageBus"/> is the seam.
/// </summary>
public sealed class InProcMessageBus : IMessageBus, IBusDiagnostics, IAsyncDisposable
{
    private readonly BusOptions _options;
    private readonly ILogger<InProcMessageBus> _logger;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TopicStatistics> _topics = new(StringComparer.Ordinal);
    private int _subscriptionCounter;
    private int _topicSlots;
    private long _totalPublishedCount;
    private long _totalDeliveredCount;
    private long _totalDroppedCount;
    private long _totalFailedCount;
    private bool _disposed;

    public InProcMessageBus(IOptions<BusOptions> options, ILogger<InProcMessageBus> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var topic = ResolveTopic(envelope.Topic);
        topic?.RecordPublish(envelope.TypeName, envelope.Sender);
        Interlocked.Increment(ref _totalPublishedCount);

        foreach (var subscription in _subscriptions.Values)
        {
            if (!TopicMatcher.IsMatch(subscription.Pattern, envelope.Topic))
            {
                continue;
            }

            if (subscription.Writer.TryWrite(envelope))
            {
                continue;
            }

            if (_options.FullMode == BusFullMode.Wait &&
                await subscription.Writer.WaitToWriteAsync(cancellationToken))
            {
                await subscription.Writer.WriteAsync(envelope, cancellationToken);
                continue;
            }

            subscription.RecordDropped();
            _logger.LogWarning(
                "Subscription {Subscription} ({Pattern}) is full; dropping message {MessageId} on {Topic}.",
                subscription.Id,
                subscription.Pattern,
                envelope.MessageId,
                envelope.Topic);
        }
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string topicPattern,
        Func<MessageEnvelope, CancellationToken, Task> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicPattern);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = $"sub-{Interlocked.Increment(ref _subscriptionCounter)}";
        var capacity = options?.Capacity ?? _options.SubscriptionCapacity;
        var channel = Channel.CreateBounded<MessageEnvelope>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var subscription = new Subscription(
            this,
            id,
            options?.Name ?? topicPattern,
            topicPattern,
            handler,
            channel);
        _subscriptions[id] = subscription;
        subscription.Start(token => PumpAsync(subscription, token));

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAsyncDisposable>(new SubscriptionHandle(this, subscription));
    }

    private async Task PumpAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            while (await subscription.Reader.WaitToReadAsync(cancellationToken))
            {
                while (subscription.Reader.TryRead(out var envelope))
                {
                    await DeliverAsync(subscription, envelope, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on unsubscribe.
        }
    }

    private async Task DeliverAsync(
        Subscription subscription,
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await subscription.Handler(envelope, cancellationToken);
                subscription.RecordDelivered();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= _options.MaxDeliveryAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Dropping message {MessageId} for {Subscription} after {Attempts} attempts.",
                        envelope.MessageId,
                        subscription.Id,
                        attempt);

                    // Never re-dead-letter a message that is already on the dead-letter topic.
                    if (envelope.Topic != BusTopics.DeadLetter)
                    {
                        await DeadLetterAsync(envelope, ex, cancellationToken);
                    }

                    subscription.RecordFailed();
                    return;
                }

                _logger.LogWarning(
                    ex,
                    "Handler for {Subscription} failed on {MessageId} (attempt {Attempt}/{MaxAttempts}); retrying.",
                    subscription.Id,
                    envelope.MessageId,
                    attempt,
                    _options.MaxDeliveryAttempts);

                if (_options.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.RetryDelay, cancellationToken);
                }
            }
        }
    }

    private async Task DeadLetterAsync(
        MessageEnvelope envelope,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableDeadLetter)
        {
            return;
        }

        var headers = new Dictionary<string, string>(envelope.Headers, StringComparer.Ordinal)
        {
            ["anima-error"] = exception.Message,
        };

        try
        {
            await PublishAsync(
                envelope with
                {
                    MessageId = Guid.NewGuid().ToString("n"),
                    Topic = BusTopics.DeadLetter,
                    Headers = headers,
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception publishException)
        {
            _logger.LogError(
                publishException,
                "Failed to publish dead letter for {MessageId}.",
                envelope.MessageId);
        }
    }

    /// <summary>
    /// Returns the existing statistics for <paramref name="name"/>, or reserves one of
    /// <see cref="BusOptions.MaxTrackedTopics"/> topic slots for it. Returns null once every
    /// slot is taken, so unique topics beyond the cap cost nothing but the publish totals.
    /// </summary>
    private TopicStatistics? ResolveTopic(string name)
    {
        if (_topics.TryGetValue(name, out var tracked))
        {
            return tracked;
        }

        var reserved = Interlocked.Increment(ref _topicSlots);
        if (reserved > _options.MaxTrackedTopics)
        {
            Interlocked.Decrement(ref _topicSlots);
            return null;
        }

        var created = new TopicStatistics(name, _options);
        if (_topics.TryAdd(name, created))
        {
            return created;
        }

        // Another publisher tracked this topic first; release the slot it already owns.
        Interlocked.Decrement(ref _topicSlots);
        return _topics[name];
    }

    public BusSnapshot GetSnapshot()
    {
        var trackedTopics = _topics.Values.Select(static statistics => statistics.ToSnapshot()).ToArray();
        var topics = trackedTopics
            .OrderByDescending(static snapshot => snapshot.PublishedCount)
            .ThenBy(static snapshot => snapshot.Topic, StringComparer.Ordinal)
            .Take(Math.Max(1, _options.MaxSnapshotTopics))
            .ToArray();
        var subscriptions = _subscriptions.Values
            .Select(static subscription => subscription.ToSnapshot())
            .OrderBy(static snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToArray();

        return new BusSnapshot(
            topics,
            subscriptions,
            Interlocked.Read(ref _totalPublishedCount),
            Interlocked.Read(ref _totalDeliveredCount),
            Interlocked.Read(ref _totalDroppedCount),
            Interlocked.Read(ref _totalFailedCount),
            trackedTopics.Length,
            TopicsTruncated: topics.Length < trackedTopics.Length);
    }

    private async Task UnsubscribeAsync(Subscription subscription)
    {
        if (_subscriptions.TryRemove(subscription.Id, out _))
        {
            await subscription.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var subscription in _subscriptions.Values)
        {
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
    }

    private sealed class SubscriptionHandle(InProcMessageBus bus, Subscription subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(bus.UnsubscribeAsync(subscription));
    }

    /// <summary>
    /// Per-topic counters with distinct type and sender sets capped at
    /// <see cref="BusOptions.MaxTypesPerTopic"/>/<see cref="BusOptions.MaxSendersPerTopic"/>.
    /// </summary>
    private sealed class TopicStatistics(string topic, BusOptions options)
    {
        private readonly ConcurrentDictionary<string, byte> _typeNames = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _senders = new(StringComparer.Ordinal);
        private readonly int _maxTypeNames = Math.Max(0, options.MaxTypesPerTopic);
        private readonly int _maxSenders = Math.Max(0, options.MaxSendersPerTopic);
        private int _typeNameCount;
        private int _senderCount;
        private long _publishedCount;
        private long _lastTimestampMilliseconds;

        public void RecordPublish(string typeName, string? sender)
        {
            Interlocked.Increment(ref _publishedCount);
            RecordDistinct(_typeNames, ref _typeNameCount, _maxTypeNames, typeName);
            if (!string.IsNullOrWhiteSpace(sender))
            {
                RecordDistinct(_senders, ref _senderCount, _maxSenders, sender);
            }

            Interlocked.Exchange(
                ref _lastTimestampMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// Adds <paramref name="value"/> while a slot is free. The counter is reserved
        /// before the insert so concurrent publishers cannot exceed the cap.
        /// </summary>
        private static void RecordDistinct(
            ConcurrentDictionary<string, byte> values,
            ref int count,
            int maximum,
            string value)
        {
            if (values.ContainsKey(value))
            {
                return;
            }

            var reserved = Interlocked.Increment(ref count);
            if (reserved > maximum)
            {
                Interlocked.Decrement(ref count);
                return;
            }

            if (!values.TryAdd(value, 0))
            {
                Interlocked.Decrement(ref count);
            }
        }

        public BusTopicSnapshot ToSnapshot()
        {
            var lastTimestamp = Interlocked.Read(ref _lastTimestampMilliseconds);
            return new BusTopicSnapshot(
                topic,
                Interlocked.Read(ref _publishedCount),
                lastTimestamp == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastTimestamp),
                _typeNames.Keys.Order(StringComparer.Ordinal).ToArray(),
                _senders.Keys.Order(StringComparer.Ordinal).ToArray());
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly InProcMessageBus _bus;
        private readonly Channel<MessageEnvelope> _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pump;
        private long _deliveredCount;
        private long _droppedCount;
        private long _failedCount;
        private long _lastActivityMilliseconds;

        public Subscription(
            InProcMessageBus bus,
            string id,
            string name,
            string pattern,
            Func<MessageEnvelope, CancellationToken, Task> handler,
            Channel<MessageEnvelope> channel)
        {
            _bus = bus;
            Id = id;
            Name = name;
            Pattern = pattern;
            Handler = handler;
            _channel = channel;
        }

        public string Id { get; }

        public string Name { get; }

        public string Pattern { get; }

        public Func<MessageEnvelope, CancellationToken, Task> Handler { get; }

        public ChannelWriter<MessageEnvelope> Writer => _channel.Writer;

        public ChannelReader<MessageEnvelope> Reader => _channel.Reader;

        public void Start(Func<CancellationToken, Task> pump)
        {
            _pump = Task.Run(() => pump(_cts.Token));
        }

        public void RecordDelivered()
        {
            Interlocked.Increment(ref _bus._totalDeliveredCount);
            RecordActivity(ref _deliveredCount);
        }

        public void RecordDropped()
        {
            Interlocked.Increment(ref _bus._totalDroppedCount);
            RecordActivity(ref _droppedCount);
        }

        public void RecordFailed()
        {
            Interlocked.Increment(ref _bus._totalFailedCount);
            RecordActivity(ref _failedCount);
        }

        public BusSubscriptionSnapshot ToSnapshot()
        {
            var lastActivity = Interlocked.Read(ref _lastActivityMilliseconds);
            return new BusSubscriptionSnapshot(
                Id,
                Name,
                Pattern,
                Interlocked.Read(ref _deliveredCount),
                Interlocked.Read(ref _droppedCount),
                Interlocked.Read(ref _failedCount),
                lastActivity == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastActivity));
        }

        private void RecordActivity(ref long counter)
        {
            Interlocked.Increment(ref counter);
            Interlocked.Exchange(
                ref _lastActivityMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            await _cts.CancelAsync();
            try
            {
                if (_pump is not null)
                {
                    await _pump;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
