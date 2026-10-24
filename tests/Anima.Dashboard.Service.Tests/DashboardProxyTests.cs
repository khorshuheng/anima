using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Anima.Dashboard.Service.Tests;

public sealed class DashboardProxyTests
{
    [Fact]
    public async Task Get_forwards_the_upstream_status_body_and_content_type()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"message\":\"workflow 42 does not exist\"}", Encoding.UTF8, "application/json"),
        }));

        var upstream = await new DashboardProxy(client, "workflow").GetAsync("api/workflows/42");

        Assert.Equal(404, upstream.StatusCode);
        Assert.Equal("application/json; charset=utf-8", upstream.ContentType);
        Assert.Equal("{\"message\":\"workflow 42 does not exist\"}", Encoding.UTF8.GetString(upstream.Body));
    }

    [Fact]
    public async Task Get_resolves_the_path_against_the_upstream_base_address()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = CreateClient(handler);

        await new DashboardProxy(client, "workflow").GetAsync("api/workflows/42/questions?open=false");

        Assert.Equal("http://upstream.test/api/workflows/42/questions?open=false", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task Get_maps_an_unreachable_upstream_to_502()
    {
        using var client = CreateClient((_, _) => throw new HttpRequestException("connection refused"));

        var upstream = await new DashboardProxy(client, "knowledge").GetAsync("api/graph");

        Assert.Equal(502, upstream.StatusCode);
        Assert.Contains("knowledge service is unavailable", Encoding.UTF8.GetString(upstream.Body));
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

        var upstream = await new DashboardProxy(client, "bus").GetAsync("api/bus/snapshot");

        Assert.Equal(504, upstream.StatusCode);
        Assert.Contains("bus service did not respond", Encoding.UTF8.GetString(upstream.Body));
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

        var status = await new DashboardProxy(client, "bus")
            .CopyEventStreamAsync("api/bus/stream?topics=anima.**", context.Response);

        Assert.Equal(200, status);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("event: ready\ndata: {}\n\n", ReadBody(context));
    }

    [Fact]
    public async Task CopyEventStream_maps_an_unreachable_upstream_to_502()
    {
        using var client = CreateClient((_, _) => throw new HttpRequestException("connection refused"));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var status = await new DashboardProxy(client, "bus")
            .CopyEventStreamAsync("api/bus/stream", context.Response);

        Assert.Equal(502, status);
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Contains("bus service is unavailable", ReadBody(context));
    }

    private static HttpClient CreateClient(StubHandler handler, TimeSpan? timeout = null) => new(handler)
    {
        BaseAddress = new Uri("http://upstream.test"),
        Timeout = timeout ?? TimeSpan.FromSeconds(10),
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
}
