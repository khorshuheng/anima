using Anima.Automation;
using Anima.Automation.Dsl;
using Anima.Automation.Playwright;
using Anima.Automation.Service;
using Anima.Automation.Service.Contracts;
using Anima.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("automation", AnimaPorts.Automation);
builder.Services.AddPlaywrightAutomation();
builder.Services.AddYamlAutomationScripts(options => options.Directory = RepoLayout.SpecsDirectory);
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.Configure<PlaywrightOptions>(builder.Configuration.GetSection("Anima:Automation:Playwright"));

var app = builder.Build();
app.UseAnimaServiceDefaults("automation");

app.MapPost("/api/sessions", async (
    SessionRequest request,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    var entry = await registry.StartAsync(request, cancellationToken);
    return Results.Created($"/api/sessions/{entry.Session.Id}", new
    {
        SessionId = entry.Session.Id,
        entry.Session.CurrentUrl,
        entry.Options.BaseUrl,
        entry.StartedAt,
    });
});

app.MapGet("/api/sessions", (SessionRegistry registry) => Results.Ok(new
{
    items = registry.List().Select(entry => new
    {
        SessionId = entry.Session.Id,
        entry.Session.CurrentUrl,
        entry.Options.BaseUrl,
        entry.StartedAt,
    }),
}));

app.MapGet("/api/sessions/{id}", (string id, SessionRegistry registry) =>
{
    var entry = registry.Get(id);
    return entry is null
        ? Results.NotFound(new { message = $"session '{id}' does not exist" })
        : Results.Ok(new
        {
            SessionId = entry.Session.Id,
            entry.Session.CurrentUrl,
            entry.Options.BaseUrl,
            entry.StartedAt,
            Cdp = entry.Session.Cdp is not null,
        });
});

app.MapDelete("/api/sessions/{id}", async (string id, SessionRegistry registry) =>
    await registry.CloseAsync(id)
        ? Results.NoContent()
        : Results.NotFound(new { message = $"session '{id}' does not exist" }));

app.MapPost("/api/sessions/{id}/scripts", async (
    string id,
    RunScriptRequest request,
    SessionRegistry registry,
    IScriptRunner runner,
    CancellationToken cancellationToken) =>
{
    var entry = registry.Get(id);
    if (entry is null)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    var result = await runner.RunAsync(
        request.Source,
        entry.Session,
        new ScriptRunOptions
        {
            Variables = request.Variables,
            StopOnError = request.StopOnError ?? true,
            BaseUrl = entry.Options.BaseUrl,
        },
        cancellationToken);

    return Results.Ok(new
    {
        result.Success,
        result.Steps,
        result.Variables,
        result.Error,
        Format = runner.Format,
    });
});

app.MapPost("/api/scripts/validate", (ValidateScriptRequest request, IScriptRunner runner) =>
{
    var result = runner.Validate(request.Source);
    return Results.Ok(new
    {
        result.IsValid,
        result.Diagnostics,
        Format = runner.Format,
    });
});

app.MapGet("/api/scripts", (ScriptCatalog catalog) => Results.Ok(new
{
    items = catalog.LoadAll().Select(script => new
    {
        script.Name,
        script.Path,
        script.IsValid,
    }),
}));

app.MapPost("/api/sessions/{id}/actions", async (
    string id,
    ActionRequest request,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    var entry = registry.Get(id);
    if (entry is null)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    var session = entry.Session;
    switch (request.Action.ToLowerInvariant())
    {
        case "goto":
        case "navigate":
            await session.NavigateAsync(Require(request.Target), cancellationToken);
            break;
        case "click":
            await session.ClickAsync(Require(request.Target), cancellationToken);
            break;
        case "fill":
            await session.FillAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "press":
            await session.PressAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "select":
            await session.SelectAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "waitfor":
            await session.WaitForAsync(Require(request.Target), WaitState.Visible, cancellationToken);
            break;
        case "eval":
            return Results.Ok(new { Output = await session.EvaluateAsync(Require(request.Target), cancellationToken) });
        default:
            return Results.BadRequest(new { message = $"unknown action '{request.Action}'" });
    }

    return Results.Ok(new { Url = session.CurrentUrl });
});

app.MapPost("/api/sessions/{id}/cdp", async (
    string id,
    CdpRequest request,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    var entry = registry.Get(id);
    if (entry is null)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    if (entry.Session.Cdp is not { } cdp)
    {
        return Results.BadRequest(new { message = "this driver does not expose CDP" });
    }

    var result = await cdp.SendAsync(request.Method, request.ParametersJson, cancellationToken);
    return Results.Ok(new { Result = result });
});

app.Run();

static string Require(string? value) => string.IsNullOrWhiteSpace(value)
    ? throw new InvalidOperationException("action requires a target/value")
    : value;
