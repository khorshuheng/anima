using System.Buffers;
using System.Text.Json;

namespace Anima.Dashboard.Service;

/// <summary>
/// Forwards one dashboard request to one upstream service. An upstream timeout
/// becomes 504 and an unreachable upstream becomes 502; every other reply keeps
/// the upstream status, content type and body.
/// </summary>
public sealed class DashboardProxy
{
    private const string JsonContentType = "application/json; charset=utf-8";

    private readonly HttpClient _client;
    private readonly string _upstream;

    public DashboardProxy(HttpClient client, string upstream)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstream);
        _client = client;
        _upstream = upstream;
    }

    /// <summary>Reads the whole upstream reply so the caller can forward it unchanged.</summary>
    public async Task<UpstreamResponse> GetAsync(string pathAndQuery, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync(
                pathAndQuery,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new UpstreamResponse(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Timeout();
        }
        catch (HttpRequestException exception)
        {
            return Unavailable(exception.Message);
        }
    }

    /// <summary>
    /// Streams an upstream server-sent-events reply to <paramref name="response"/>,
    /// flushing every chunk so the browser receives events as they happen. Returns
    /// the status written to the client.
    /// </summary>
    public async Task<int> CopyEventStreamAsync(
        string pathAndQuery,
        HttpResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        HttpResponseMessage upstream;
        try
        {
            upstream = await _client.GetAsync(
                pathAndQuery,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Timeout().WriteAsync(response, cancellationToken);
            return response.StatusCode;
        }
        catch (HttpRequestException exception)
        {
            await Unavailable(exception.Message).WriteAsync(response, cancellationToken);
            return response.StatusCode;
        }

        using (upstream)
        {
            response.StatusCode = (int)upstream.StatusCode;
            response.Headers.CacheControl = "no-cache";
            if (upstream.Content.Headers.ContentType?.ToString() is { } contentType)
            {
                response.ContentType = contentType;
            }

            await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return response.StatusCode;
        }
    }

    private UpstreamResponse Timeout() => new(
        StatusCodes.Status504GatewayTimeout,
        JsonContentType,
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            message = $"{_upstream} service did not respond within the dashboard upstream timeout",
        }));

    private UpstreamResponse Unavailable(string detail) => new(
        StatusCodes.Status502BadGateway,
        JsonContentType,
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            message = $"{_upstream} service is unavailable: {detail}",
        }));
}
