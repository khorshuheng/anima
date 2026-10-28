using System.Text.Json;
using System.Text.Json.Serialization;
using Anima.Deploy;
using Anima.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("deploy", AnimaPorts.Deploy);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enum values are part of the agent-facing API; keep them readable.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddDeployHooks();
builder.Services.Configure<DeployOptions>(builder.Configuration.GetSection("Anima:Deploy"));

var app = builder.Build();
app.UseAnimaServiceDefaults("deploy");

app.MapPost("/hooks/git/{provider}", async (
    string provider,
    HttpContext context,
    IDeployHookSink sink,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(context.Request.Body);
    string payload = await reader.ReadToEndAsync(cancellationToken);
    var headers = context.Request.Headers.ToDictionary(
        header => header.Key,
        header => header.Value.ToString(),
        StringComparer.OrdinalIgnoreCase);

    try
    {
        var acceptance = await sink.AcceptAsync(provider, payload, headers, cancellationToken);
        return Results.Accepted($"/api/runs/{acceptance.RunId}", new
        {
            acceptance.RunId,
            acceptance.Matched,
            acceptance.Message,
            acceptance.RunIds,
        });
    }
    catch (DeploySignatureException exception)
    {
        return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (DeployValidationException exception)
    {
        return Results.UnprocessableEntity(new { message = exception.Message });
    }
});

app.MapGet("/api/runs", (IDeployRunStore store) => Results.Ok(new { items = store.List() }));

app.MapGet("/api/runs/{id}", (string id, IDeployRunStore store) =>
{
    var run = store.Find(id);
    return run is null
        ? Results.NotFound(new { message = $"run '{id}' does not exist" })
        : Results.Ok(run);
});

app.MapGet("/api/runs/{id}/log", (string id, IDeployRunStore store) =>
{
    var run = store.Find(id);
    return run is null
        ? Results.NotFound(new { message = $"run '{id}' does not exist" })
        : Results.Ok(new { runId = id, lines = store.GetLog(id) });
});

await app.RunAsync();
