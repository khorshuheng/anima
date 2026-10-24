using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Contracts;

namespace Anima.Bus;

/// <summary>
/// Shared wire mapping for the HTTP bus and SSE stream. JSON payloads are sent as
/// JSON; other content types travel as base64.
/// </summary>
public static class BusEnvelopeJson
{
    public static JsonObject ToJson(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var json = new JsonObject
        {
            ["messageId"] = envelope.MessageId,
            ["topic"] = envelope.Topic,
            ["typeName"] = envelope.TypeName,
            ["contentType"] = envelope.ContentType,
            ["timestamp"] = envelope.Timestamp,
            ["correlationId"] = envelope.CorrelationId,
            ["causationId"] = envelope.CausationId,
            ["sender"] = envelope.Sender,
            ["recipient"] = envelope.Recipient,
            ["replyTo"] = envelope.ReplyTo,
            ["payloadBase64"] = Convert.ToBase64String(envelope.Payload.Span),
        };

        var headers = new JsonObject();
        foreach (var pair in envelope.Headers)
        {
            headers[pair.Key] = pair.Value;
        }

        json["headers"] = headers;

        if (IsJsonContentType(envelope.ContentType) && !envelope.Payload.IsEmpty)
        {
            var text = Encoding.UTF8.GetString(envelope.Payload.Span);
            try
            {
                json["payloadJson"] = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // Keep the base64 form when the payload is not valid JSON.
            }
        }

        return json;
    }

    public static MessageEnvelope FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("bus envelope must be a JSON object");
        }

        var payload = ReadPayload(element);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("headers", out var headersElement) &&
            headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersElement.EnumerateObject())
            {
                headers[header.Name] = header.Value.ToString();
            }
        }

        return new MessageEnvelope
        {
            MessageId = GetString(element, "messageId") ?? Guid.NewGuid().ToString("n"),
            Topic = GetString(element, "topic") ?? throw new FormatException("bus envelope requires topic"),
            TypeName = GetString(element, "typeName") ?? throw new FormatException("bus envelope requires typeName"),
            ContentType = GetString(element, "contentType") ?? ContractContentTypes.ProtobufJson,
            Payload = payload,
            Timestamp = element.TryGetProperty("timestamp", out var timestamp) &&
                        timestamp.TryGetDateTimeOffset(out var parsedTimestamp)
                ? parsedTimestamp
                : DateTimeOffset.UtcNow,
            CorrelationId = GetString(element, "correlationId"),
            CausationId = GetString(element, "causationId"),
            Sender = GetString(element, "sender"),
            Recipient = GetString(element, "recipient"),
            ReplyTo = GetString(element, "replyTo"),
            Headers = headers,
        };
    }

    public static bool IsJsonContentType(string contentType)
        => contentType is ContractContentTypes.ProtobufJson or ContractContentTypes.Json;

    private static ReadOnlyMemory<byte> ReadPayload(JsonElement element)
    {
        if (element.TryGetProperty("payloadJson", out var payloadJson) &&
            payloadJson.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return Encoding.UTF8.GetBytes(payloadJson.GetRawText());
        }

        if (element.TryGetProperty("payloadBase64", out var base64) &&
            base64.ValueKind == JsonValueKind.String)
        {
            return Convert.FromBase64String(base64.GetString() ?? string.Empty);
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
