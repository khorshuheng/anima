using System.Text.Json;
using Anima.Dashboard.Service;
using Anima.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("dashboard", AnimaPorts.Dashboard);
builder.Services.AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>();
builder.Services.AddSingleton<GoalExplorer>();
builder.Services.AddSingleton<WorkflowSummaryService>();

AddUpstream(builder.Services, DashboardUpstreams.Workflow, options => (options.WorkflowBaseUrl, options.UpstreamTimeout));
AddUpstream(builder.Services, DashboardUpstreams.Knowledge, options => (options.KnowledgeBaseUrl, options.UpstreamTimeout));
AddUpstream(builder.Services, DashboardUpstreams.Bus, options => (options.BusBaseUrl, options.UpstreamTimeout));
// The SSE connection stays open until the browser leaves, so its client has no timeout.
AddUpstream(builder.Services, DashboardUpstreams.BusStream, options => (options.BusBaseUrl, Timeout.InfiniteTimeSpan));

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

app.MapGet("/api/dashboard/workflows/{id:long}/summary", async (
    long id,
    WorkflowSummaryService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var summary = await service.GetAsync(id, cancellationToken);
        return summary is null
            ? Results.NotFound(new { message = $"workflow {id} does not exist" })
            : Results.Ok(summary);
    }
    catch (HttpRequestException)
    {
        return Results.Json(
            new { message = "workflow service is unavailable" },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (JsonException)
    {
        return Results.Json(
            new { message = "workflow service returned an invalid summary response" },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/dashboard/workflows/{**path}", async (
    string? path,
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Workflow),
        DashboardUpstreams.Workflow,
        options.Value.UpstreamTimeout);
    var upstream = await proxy.GetAsync(
        DashboardRoutes.Workflows(path, context.Request.QueryString.Value),
        cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/graph", async (
    int? maxNodes,
    int? maxEdges,
    bool? newest,
    long? sinceId,
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Knowledge),
        DashboardUpstreams.Knowledge,
        options.Value.UpstreamTimeout);
    var upstream = await proxy.GetAsync(
        DashboardRoutes.Graph(maxNodes, maxEdges, newest, sinceId),
        cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/nodes", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Knowledge),
        DashboardUpstreams.Knowledge,
        options.Value.UpstreamTimeout);
    var upstream = await proxy.GetAsync(
        DashboardRoutes.Nodes(context.Request.QueryString.Value),
        cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/search", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Knowledge),
        DashboardUpstreams.Knowledge,
        options.Value.UpstreamTimeout);
    var upstream = await proxy.GetAsync(
        DashboardRoutes.Search(context.Request.QueryString.Value),
        cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/goals", async (
    long? repoId,
    bool? resolved,
    GoalExplorer explorer,
    CancellationToken cancellationToken) =>
    Results.Ok(await explorer.GetGoalsAsync(repoId, resolved, cancellationToken)));

app.MapGet("/api/dashboard/kinds", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Knowledge),
        DashboardUpstreams.Knowledge,
        options.Value.UpstreamTimeout);
    var upstream = await proxy.GetAsync(DashboardRoutes.Kinds(), cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/bus", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.Bus),
        DashboardUpstreams.Bus,
        options.Value.UpstreamTimeout);
    var upstream = await proxy.GetAsync(DashboardRoutes.BusSnapshot(), cancellationToken);
    await upstream.WriteAsync(context.Response, cancellationToken);
});

app.MapGet("/api/dashboard/bus/stream", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<DashboardOptions> options,
    CancellationToken cancellationToken) =>
{
    var proxy = new DashboardProxy(
        httpClientFactory.CreateClient(DashboardUpstreams.BusStream),
        DashboardUpstreams.Bus,
        options.Value.UpstreamTimeout);
    await proxy.CopyEventStreamAsync(
        DashboardRoutes.BusStream(context.Request.QueryString.Value),
        context.Response,
        cancellationToken);
});

app.Run();

// Registers one named upstream client. The base URL and timeout are read from the
// validated DashboardOptions on first use, not from an unvalidated configuration
// snapshot taken at startup.
static void AddUpstream(
    IServiceCollection services,
    string name,
    Func<DashboardOptions, (string BaseUrl, TimeSpan Timeout)> select)
{
    services.AddHttpClient(name, (provider, client) =>
    {
        var (baseUrl, timeout) = select(provider.GetRequiredService<IOptions<DashboardOptions>>().Value);
        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        client.Timeout = timeout;
    });
}
