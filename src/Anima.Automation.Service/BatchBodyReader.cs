using System.Text.Json;
using Anima.Automation.Service.Contracts;

namespace Anima.Automation.Service;

/// <summary>Batch body as sent, plus a reason when it could not be read.</summary>
internal readonly record struct ParsedBatch(
    IReadOnlyList<ActionRequest>? Steps,
    bool? StopOnError,
    string? Error);

/// <summary>
/// Reads a batch body in either accepted shape: a bare array of actions, or an
/// object carrying <c>steps</c> and <c>stopOnError</c>. A bare array is the shape a
/// caller would write first, so both are supported rather than picking one.
/// </summary>
internal static class BatchBodyReader
{
    /// <summary>Batches are step lists, so anything this large is a mistake or an attack.</summary>
    private const int MaxBodyChars = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ParsedBatch> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reader = new StreamReader(request.Body);
        char[] buffer = new char[MaxBodyChars + 1];
        int total = 0;
        int read = 0;

        while (total < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        if (total > MaxBodyChars)
        {
            return new ParsedBatch(null, null, $"batch body exceeds {MaxBodyChars} characters");
        }

        string body = new(buffer, 0, total);
        var trimmed = body.AsSpan().TrimStart();

        if (trimmed.IsEmpty)
        {
            return new ParsedBatch(null, null, "batch body is empty");
        }

        try
        {
            if (trimmed[0] == '[')
            {
                var steps = JsonSerializer.Deserialize<List<ActionRequest>>(body, JsonOptions);
                return new ParsedBatch(steps, null, null);
            }

            var batch = JsonSerializer.Deserialize<BatchActionRequest>(body, JsonOptions);
            return new ParsedBatch(batch?.Steps, batch?.StopOnError, null);
        }
        catch (JsonException exception)
        {
            return new ParsedBatch(
                null,
                null,
                $"batch body must be an array of actions or an object with steps: {exception.Message}");
        }
    }
}
