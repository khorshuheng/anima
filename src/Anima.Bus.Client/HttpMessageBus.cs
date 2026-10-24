using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Bus.Client;

/// <summary>
/// <see cref="IMessageBus"/> over the bus service: publish via HTTP, subscribe via
/// SSE. Subscriptions reconnect on stream loss; handlers never block the stream.
/// </summary>
public sealed class HttpMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly BusClientOptions _options;
    private readonly ILogger<HttpMessageBus> _logger;
    private readonly ConcurrentDictionary<string, HttpSubscription> _subscriptions = new(StringComparer.Ordinal);
    private int _counter;
    private bool _disposed;

    public HttpMessageBus(
        HttpClient http,
        IOptions<BusClientOptions> options,
        ILogger<HttpMessageBus> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.PublishTimeout);

        try
        {
            using var content = JsonContent.Create(
                BusEnvelopeJson.ToJson(envelope),
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var response = await _http.PostAsync("api/bus/publish", content, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                throw new InvalidOperationException(
                    $"bus publish failed ({(int)response.StatusCode}): {body}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"bus publish to '{_options.BaseUrl}' timed out after {_options.PublishTimeout.TotalSeconds:0.#}s");
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        string topicPattern,
        Func<MessageEnvelope, CancellationToken, Task> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicPattern);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var id = $"http-sub-{Interlocked.Increment(ref _counter)}";
        var subscription = new HttpSubscription(
            id,
            options?.Name ?? topicPattern,
            topicPattern,
            handler,
            _http,
            _options,
            _logger);
        _subscriptions[id] = subscription;
        subscription.Start();

        try
        {
            // Do not return before the SSE stream is registered server-side, or a
            // reply published immediately after Subscribe could be missed.
            await subscription.WaitForReadyAsync(_options.SubscriptionReadyTimeout, cancellationToken);
        }
        catch
        {
            _subscriptions.TryRemove(id, out _);
            await subscription.DisposeAsync();
            throw;
        }

        return new Handle(this, subscription);
    }

    private async Task RemoveAsync(HttpSubscription subscription)
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

    private sealed class Handle(HttpMessageBus bus, HttpSubscription subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(bus.RemoveAsync(subscription));
    }

    private sealed class HttpSubscription : IAsyncDisposable
    {
        private readonly Channel<MessageEnvelope> _channel;
        private readonly HttpClient _http;
        private readonly BusClientOptions _options;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _reader;
        private Task? _pump;
        private bool _disposed;

        public HttpSubscription(
            string id,
            string name,
            string pattern,
            Func<MessageEnvelope, CancellationToken, Task> handler,
            HttpClient http,
            BusClientOptions options,
            ILogger logger)
        {
            Id = id;
            Name = name;
            Pattern = pattern;
            Handler = handler;
            _http = http;
            _options = options;
            _logger = logger;
            _channel = Channel.CreateBounded<MessageEnvelope>(new BoundedChannelOptions(options.SubscriptionCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        public string Id { get; }

        public string Name { get; }

        public string Pattern { get; }

        public Func<MessageEnvelope, CancellationToken, Task> Handler { get; }

        public void Start()
        {
            _reader = Task.Run(() => ReadAsync(_cts.Token));
            _pump = Task.Run(() => PumpAsync(_cts.Token));
        }

        public Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => _ready.Task.WaitAsync(timeout, cancellationToken);

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var response = await _http.GetAsync(
                        $"api/bus/stream?topics={Uri.EscapeDataString(Pattern)}",
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await foreach (var envelope in SseEnvelopeReader.ReadAsync(
                                       stream,
                                       () =>
                                       {
                                           _ready.TrySetResult();
                                           return Task.CompletedTask;
                                       },
                                       cancellationToken))
                    {
                        await _channel.Writer.WriteAsync(envelope, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Bus subscription {Subscription} for {Pattern} disconnected; reconnecting in {Delay}.",
                        Name,
                        Pattern,
                        _options.ReconnectDelay);
                }

                try
                {
                    await Task.Delay(_options.ReconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _channel.Writer.TryComplete();
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var envelope in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        await Handler(envelope, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(
                            exception,
                            "Bus subscription {Subscription} failed to handle {MessageId} on {Topic}.",
                            Name,
                            envelope.MessageId,
                            envelope.Topic);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on dispose.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _channel.Writer.TryComplete();
            await _cts.CancelAsync();

            foreach (var task in new[] { _reader, _pump })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }

            _cts.Dispose();
        }
    }
}
