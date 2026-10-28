using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Anima.Bus.Client;

/// <summary>Reads <c>event: message</c> / <c>data: {...}</c> frames from an SSE stream.</summary>
public static class SseEnvelopeReader
{
    public static async IAsyncEnumerable<MessageEnvelope> ReadAsync(
        Stream stream,
        Func<Task>? onReady = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var data = new StringBuilder();
        string? eventName = null;

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (string.Equals(eventName, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    if (onReady is not null)
                    {
                        await onReady();
                    }
                }
                else if (data.Length > 0)
                {
                    var envelope = ParseFrame(data.ToString());
                    if (envelope is not null)
                    {
                        yield return envelope;
                    }
                }

                data.Clear();
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line.AsSpan(6).Trim().ToString();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (!string.Equals(eventName, "ready", StringComparison.OrdinalIgnoreCase) && data.Length > 0)
        {
            var envelope = ParseFrame(data.ToString());
            if (envelope is not null)
            {
                yield return envelope;
            }
        }
    }

    private static MessageEnvelope? ParseFrame(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return BusEnvelopeJson.FromJson(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
