using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Anima.Bus;

public sealed class InProcAgentMailboxFactory : IAgentMailboxFactory
{
    private readonly IMessageBus _bus;
    private readonly ILogger<InProcAgentMailboxFactory> _logger;

    public InProcAgentMailboxFactory(IMessageBus bus, ILogger<InProcAgentMailboxFactory> logger)
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
        var mailbox = new InProcAgentMailbox(agentId, _bus, _logger);
        await mailbox.StartAsync(cancellationToken);
        return mailbox;
    }
}

internal sealed class InProcAgentMailbox : IAgentMailbox
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

    public InProcAgentMailbox(string agentId, IMessageBus bus, ILogger logger)
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
                    _logger.LogWarning("Mailbox for {AgentId} is closed; dropping {MessageId}.",
                        AgentId, envelope.MessageId);
                }

                return Task.CompletedTask;
            },
            new SubscriptionOptions { Name = $"mailbox:{AgentId}" },
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
