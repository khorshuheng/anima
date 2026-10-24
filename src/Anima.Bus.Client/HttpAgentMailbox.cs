using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Anima.Bus.Client;

public sealed class HttpAgentMailboxFactory : IAgentMailboxFactory
{
    private readonly IMessageBus _bus;
    private readonly ILogger<HttpAgentMailboxFactory> _logger;

    public HttpAgentMailboxFactory(IMessageBus bus, ILogger<HttpAgentMailboxFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(logger);
        _bus = bus;
        _logger = logger;
    }

    public async ValueTask<IAgentMailbox> CreateAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var mailbox = new HttpAgentMailbox(agentId, _bus, _logger);
        await mailbox.StartAsync(cancellationToken);
        return mailbox;
    }
}

internal sealed class HttpAgentMailbox : IAgentMailbox
{
    private readonly IMessageBus _bus;
    private readonly ILogger _logger;
    private readonly Channel<MessageEnvelope> _channel =
        Channel.CreateUnbounded<MessageEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private IAsyncDisposable? _subscription;

    public HttpAgentMailbox(string agentId, IMessageBus bus, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(logger);
        AgentId = agentId.Trim().ToLowerInvariant();
        _bus = bus;
        _logger = logger;
    }

    public string AgentId { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await _bus.SubscribeAsync(
            BusTopics.AgentInbox(AgentId),
            (envelope, _) =>
            {
                if (!_channel.Writer.TryWrite(envelope))
                {
                    _logger.LogWarning(
                        "Mailbox for {AgentId} is closed; dropping {MessageId}.",
                        AgentId,
                        envelope.MessageId);
                }

                return Task.CompletedTask;
            },
            new SubscriptionOptions { Name = $"http-mailbox:{AgentId}" },
            cancellationToken);
    }

    public ValueTask SendAsync(
        string recipient,
        MessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentNullException.ThrowIfNull(envelope);

        return _bus.PublishAsync(
            envelope with
            {
                Recipient = recipient,
                Topic = BusTopics.AgentInbox(recipient),
            },
            cancellationToken);
    }

    public IAsyncEnumerable<MessageEnvelope> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }
    }
}
