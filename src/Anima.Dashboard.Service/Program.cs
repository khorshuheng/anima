using Anima.Dashboard.Service;
using Anima.Hosting;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("dashboard", AnimaPorts.Dashboard);

var options = builder.Configuration.GetSection("Anima:Dashboard").Get<DashboardOptions>()
    ?? new DashboardOptions();

builder.Services.AddHttpClient(
    DashboardUpstreams.Workflow,
    client => Configure(client, options.WorkflowBaseUrl, options.UpstreamTimeout));
builder.Services.AddHttpClient(
    DashboardUpstreams.Knowledge,
    client => Configure(client, options.KnowledgeBaseUrl, options.UpstreamTimeout));
builder.Services.AddHttpClient(
    DashboardUpstreams.Bus,
    client => Configure(client, options.BusBaseUrl, options.UpstreamTimeout));
builder.Services.AddHttpClient(
    DashboardUpstreams.BusStream,
    client => Configure(client, options.BusBaseUrl, Timeout.InfiniteTimeSpan));

var app = builder.Build();
// The dashboard owns `/` (ui/dist), so it does not map the shared service-info root.
app.UseAnimaServiceDefaults("dashboard", mapServiceInfo: false);

var uiRoot = Path.Combine(RepoLayout.Root, "ui", "dist");
if (Directory.Exists(uiRoot))
{
    var uiFiles = new PhysicalFileProvider(uiRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = uiFiles });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = uiFiles,
        OnPrepareResponse = context =>
        {
            var headers = context.Context.Response.Headers;
            if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "no-cache";
            }
            else if (context.Context.Request.Path.StartsWithSegments("/assets"))
            {
                headers.CacheControl = "public,max-age=31536000,immutable";
            }
        },
    });
}
else
{
    app.Logger.LogWarning("UI build not found at {UiRoot}; run 'make ui-build'.", uiRoot);
}

app.MapGet("/api/dashboard/workflows/{**path}", async (
    string? path,
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Workflow),
        DashboardUpstreams.Workflow);
    var upstream = await proxy.GetAsync(
        DashboardRoutes.Workflows(path, context.Request.QueryString.Value),
        cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/graph", async (
    int? maxNodes,
    int? maxEdges,
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Knowledge),
        DashboardUpstreams.Knowledge);
    var upstream = await proxy.GetAsync(
        DashboardRoutes.Graph(maxNodes, maxEdges),
        cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/bus", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Bus),
        DashboardUpstreams.Bus);
    var upstream = await proxy.GetAsync(DashboardRoutes.BusSnapshot(), cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/bus/stream", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.BusStream),
        DashboardUpstreams.Bus);
    await proxy.CopyEventStreamAsync(
        DashboardRoutes.BusStream(context.Request.QueryString.Value),
        context.Response,
        cancellationToken);
});

app.Run();

static void Configure(HttpClient client, string baseUrl, TimeSpan timeout)
{
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    client.Timeout = timeout;
}
