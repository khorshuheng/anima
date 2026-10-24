namespace Anima.Bus;

public sealed record BusRequestOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional explicit reply topic; otherwise a private topic is generated.</summary>
    public string? ReplyTopic { get; init; }
}

public sealed class BusRequestTimeoutException(string message) : Exception(message);

/// <summary>Request/response over the bus, correlated by a private reply topic.</summary>
public interface IBusRequester
{
    Task<MessageEnvelope> AskAsync(
        MessageEnvelope request,
        BusRequestOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class BusRequester : IBusRequester
{
    private readonly IBusPublisher _publisher;
    private readonly IBusSubscriber _subscriber;

    public BusRequester(IBusPublisher publisher, IBusSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(subscriber);
        _publisher = publisher;
        _subscriber = subscriber;
    }

    public async Task<MessageEnvelope> AskAsync(
        MessageEnvelope request,
        BusRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new BusRequestOptions();

        var replyTopic = options.ReplyTopic ?? $"{BusTopics.ReplyPrefix}.{request.MessageId}";
        var reply = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await _subscriber.SubscribeAsync(
            replyTopic,
            (envelope, _) =>
            {
                reply.TrySetResult(envelope);
                return Task.CompletedTask;
            },
            new SubscriptionOptions { Name = $"reply:{request.MessageId}" },
            cancellationToken);

        await _publisher.PublishAsync(
            request with
            {
                ReplyTo = replyTopic,
                CorrelationId = request.CorrelationId ?? request.MessageId,
            },
            cancellationToken);

        try
        {
            return await reply.Task.WaitAsync(options.Timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new BusRequestTimeoutException(
                $"no reply on '{replyTopic}' within {options.Timeout.TotalSeconds:0.#}s for '{request.Topic}'");
        }
    }
}
