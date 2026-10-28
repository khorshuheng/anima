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
    private readonly TimeSpan _requestTimeout;

    public DashboardProxy(HttpClient client, string upstream, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstream);
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                requestTimeout,
                "The dashboard upstream timeout must be positive.");
        }

        _client = client;
        _upstream = upstream;
        _requestTimeout = requestTimeout;
    }

    /// <summary>
    /// Reads the whole upstream reply so the caller can forward it unchanged. One
    /// linked deadline covers both the reply head and the body read, because
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> ends the client timeout
    /// once the headers arrive.
    /// </summary>
    public async Task<UpstreamResponse> GetAsync(string pathAndQuery, CancellationToken cancellationToken = default)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_requestTimeout);

        try
        {
            using var response = await _client.GetAsync(
                pathAndQuery,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout.Token);
            byte[] body = await response.Content.ReadAsByteArrayAsync(requestTimeout.Token);
            return new UpstreamResponse(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Timeout();
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// Streams an upstream server-sent-events reply to <paramref name="response"/>,
    /// flushing every chunk so the browser receives events as they happen. Returns
    /// the status written to the client. The stream runs until either side closes, so
    /// it deliberately uses no request deadline.
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
        catch (HttpRequestException)
        {
            await Unavailable().WriteAsync(response, cancellationToken);
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
            byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
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

    /// <summary>
    /// Generic 502. Host and socket details stay out of the browser: they leak internal
    /// addresses and change shape with the runtime.
    /// </summary>
    private UpstreamResponse Unavailable() => new(
        StatusCodes.Status502BadGateway,
        JsonContentType,
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            message = $"{_upstream} service is unavailable",
        }));
}
