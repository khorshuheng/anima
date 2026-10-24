using System.Text.Json;

namespace Anima.Bus.Service.Contracts;

public sealed record PublishRequest(
    string Topic,
    string TypeName,
    JsonElement? Payload,
    string? PayloadBase64 = null,
    string? ContentType = null,
    string? Sender = null,
    string? Recipient = null,
    string? CorrelationId = null,
    string? CausationId = null,
    string? ReplyTo = null,
    Dictionary<string, string>? Headers = null);

public sealed record ValidateRequest(string TypeName, string Json);

internal static class BusJson
{
    public static readonly JsonSerializerOptions Sse = new(JsonSerializerDefaults.Web);
}
