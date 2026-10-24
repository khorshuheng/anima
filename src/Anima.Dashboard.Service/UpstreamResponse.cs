namespace Anima.Dashboard.Service;

/// <summary>A dashboard upstream reply: gateway status, content type and buffered body.</summary>
public sealed record UpstreamResponse(int StatusCode, string? ContentType, byte[] Body)
{
    /// <summary>Copies this reply onto the dashboard HTTP response.</summary>
    public async Task WriteAsync(HttpResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.StatusCode = StatusCode;
        if (!string.IsNullOrEmpty(ContentType))
        {
            response.ContentType = ContentType;
        }

        if (Body.Length > 0)
        {
            await response.Body.WriteAsync(Body, cancellationToken);
        }
    }
}
