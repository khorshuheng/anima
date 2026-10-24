using Anima.Contracts;

namespace Anima.Bus;

/// <summary>
/// Transport-neutral message wrapper. Payload bytes are encoded using a contract
/// from the runtime schema catalog, identified by <see cref="TypeName"/>.
/// </summary>
public sealed record MessageEnvelope
{
    public required string MessageId { get; init; }

    public required string Topic { get; init; }

    public required string TypeName { get; init; }

    public string ContentType { get; init; } = ContractContentTypes.ProtobufJson;

    public ReadOnlyMemory<byte> Payload { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string? CorrelationId { get; init; }

    /// <summary>Topic a request wants its reply on.</summary>
    public string? ReplyTo { get; init; }

    public string? CausationId { get; init; }

    public string? Sender { get; init; }

    public string? Recipient { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static MessageEnvelope Create(
        string topic,
        string typeName,
        ReadOnlyMemory<byte> payload,
        string contentType = ContractContentTypes.ProtobufJson,
        string? sender = null,
        string? recipient = null,
        string? correlationId = null,
        string? causationId = null,
        string? replyTo = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        return new MessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("n"),
            Topic = topic,
            TypeName = typeName,
            ContentType = contentType,
            Payload = payload,
            Sender = sender,
            Recipient = recipient,
            CorrelationId = correlationId,
            CausationId = causationId,
            ReplyTo = replyTo,
            Headers = headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }
}
