namespace Anima.Bus;

/// <summary>Point-to-point inbox for one agent.</summary>
public interface IAgentMailbox : IAsyncDisposable
{
    string AgentId { get; }

    ValueTask SendAsync(string recipient, MessageEnvelope envelope, CancellationToken cancellationToken = default);

    IAsyncEnumerable<MessageEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Read one message, or null when the timeout elapses.</summary>
    async Task<MessageEnvelope?> ReceiveAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await foreach (var envelope in ReadAllAsync(timeoutSource.Token))
            {
                return envelope;
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

public interface IAgentMailboxFactory
{
    ValueTask<IAgentMailbox> CreateAsync(string agentId, CancellationToken cancellationToken = default);
}
