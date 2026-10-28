using System.Text.Json;

namespace Anima.Automation.Service;

/// <summary>
/// Server-sent-event framing for live session event logs, mirroring the bus
/// stream: one <c>event: browser-event</c> frame per captured event.
/// </summary>
public static class SessionEventSse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteReadyAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        await response.WriteAsync("event: ready\ndata: {}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    public static async Task WriteEventAsync(
        HttpResponse response,
        BrowserEvent item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(item);

        string payload = JsonSerializer.Serialize(item, JsonOptions);
        await response.WriteAsync($"event: browser-event\ndata: {payload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>Comment frame; keeps idle streams alive through intermediaries.</summary>
    public static async Task WriteKeepAliveAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await response.WriteAsync(": keepalive\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Final frame: the session closed, so the client must not wait for more.
    /// Without it a closed session looks identical to an idle one.
    /// </summary>
    public static async Task WriteClosedAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await response.WriteAsync("event: closed\ndata: {}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
