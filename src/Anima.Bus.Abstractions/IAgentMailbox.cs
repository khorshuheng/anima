namespace Anima.Bus;

/// <summary>Point-to-point inbox for one agent.</summary>
public interface IAgentMailbox : IAsyncDisposable
{
    string AgentId { get; }

    ValueTask SendAsync(string recipient, MessageEnvelope envelope, CancellationToken cancellationToken = default);

    IAsyncEnumerable<MessageEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
}

public interface IAgentMailboxFactory
{
    ValueTask<IAgentMailbox> CreateAsync(string agentId, CancellationToken cancellationToken = default);
}
