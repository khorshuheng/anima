using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Anima.Dashboard.Service.Tests;

public sealed class DashboardProxyTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Get_forwards_the_upstream_status_body_and_content_type()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"message\":\"workflow 42 does not exist\"}", Encoding.UTF8, "application/json"),
        }));

        var upstream = await CreateProxy(client, "workflow").GetAsync("api/workflows/42");

        Assert.Equal(404, upstream.StatusCode);
        Assert.Equal("application/json; charset=utf-8", upstream.ContentType);
        Assert.Equal("{\"message\":\"workflow 42 does not exist\"}", Encoding.UTF8.GetString(upstream.Body));
    }

    [Fact]
    public async Task Get_resolves_the_path_against_the_upstream_base_address()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = CreateClient(handler);

        await CreateProxy(client, "workflow").GetAsync("api/workflows/42/questions?open=false");

        Assert.Equal("http://upstream.test/api/workflows/42/questions?open=false", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task Vocabulary_routes_resolve_against_the_knowledge_upstream()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = CreateClient(handler);
        var proxy = CreateProxy(client, "knowledge");

        await proxy.GetAsync(DashboardRoutes.Vocabulary());
        Assert.Equal("http://upstream.test/api/vocabulary", handler.LastRequestUri?.ToString());

        await proxy.GetAsync(DashboardRoutes.EdgeTypes());
        Assert.Equal("http://upstream.test/api/edge-types", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task Get_maps_an_unreachable_upstream_to_a_generic_502()
    {
        using var client = CreateClient(
            (_, _) => throw new HttpRequestException("connection refused (127.0.0.1:7411)"));

        var upstream = await CreateProxy(client, "knowledge").GetAsync("api/graph");

        Assert.Equal(502, upstream.StatusCode);
        Assert.Equal(
            "{\"message\":\"knowledge service is unavailable\"}",
            Encoding.UTF8.GetString(upstream.Body));
    }

    [Fact]
    public async Task Get_maps_an_upstream_timeout_to_504()
    {
        using var client = CreateClient(
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            TimeSpan.FromMilliseconds(100));

        var upstream = await CreateProxy(client, "bus").GetAsync("api/bus/snapshot");

        Assert.Equal(504, upstream.StatusCode);
        Assert.Contains("bus service did not respond", Encoding.UTF8.GetString(upstream.Body));
    }

    [Fact]
    public async Task Get_applies_the_request_timeout_to_the_body_read()
    {
        // Headers arrive immediately, so only the proxy's own deadline bounds the body:
        // the HttpClient timeout ends when the response head is read.
        using var client = CreateClient(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StalledContent(TimeSpan.FromSeconds(5)),
            }),
            Timeout.InfiniteTimeSpan);
        var proxy = CreateProxy(client, "knowledge", TimeSpan.FromMilliseconds(100));

        var upstream = await proxy.GetAsync("api/graph");

        Assert.Equal(504, upstream.StatusCode);
        Assert.Contains("knowledge service did not respond", Encoding.UTF8.GetString(upstream.Body));
    }

    [Fact]
    public async Task CopyEventStream_forwards_status_content_type_and_events()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: ready\ndata: {}\n\n", Encoding.UTF8, "text/event-stream"),
        }));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        int status = await CreateProxy(client, "bus")
            .CopyEventStreamAsync("api/bus/stream?topics=anima.**", context.Response);

        Assert.Equal(200, status);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("event: ready\ndata: {}\n\n", ReadBody(context));
    }

    [Fact]
    public async Task CopyEventStream_maps_an_unreachable_upstream_to_a_generic_502()
    {
        using var client = CreateClient((_, _) => throw new HttpRequestException("connection refused"));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        int status = await CreateProxy(client, "bus")
            .CopyEventStreamAsync("api/bus/stream", context.Response);

        Assert.Equal(502, status);
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Equal("{\"message\":\"bus service is unavailable\"}", ReadBody(context));
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_request_timeout()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DashboardProxy(client, "bus", TimeSpan.Zero));
    }

    private static DashboardProxy CreateProxy(
        HttpClient client,
        string upstream,
        TimeSpan? requestTimeout = null) => new(client, upstream, requestTimeout ?? RequestTimeout);

    private static HttpClient CreateClient(StubHandler handler, TimeSpan? timeout = null) => new(handler)
    {
        BaseAddress = new Uri("http://upstream.test"),
        Timeout = timeout ?? RequestTimeout,
    };

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null) => CreateClient(new StubHandler(respond), timeout);

    private static string ReadBody(DefaultHttpContext context)
    {
        var body = (MemoryStream)context.Response.Body;
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return respond(request, cancellationToken);
        }
    }

    /// <summary>Reply head is instant; the body only completes when the reader cancels.</summary>
    private sealed class StalledContent(TimeSpan stall) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.Delay(stall);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
            => await Task.Delay(stall, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
