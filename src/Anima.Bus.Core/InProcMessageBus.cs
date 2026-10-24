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
public sealed class InProcMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly BusOptions _options;
    private readonly ILogger<InProcMessageBus> _logger;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private int _subscriptionCounter;
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
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await subscription.Handler(envelope, cancellationToken);
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

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Channel<MessageEnvelope> _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pump;

        public Subscription(
            string id,
            string name,
            string pattern,
            Func<MessageEnvelope, CancellationToken, Task> handler,
            Channel<MessageEnvelope> channel)
        {
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
