using System.Threading.Channels;
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

// Contradictory browser configuration must fail at boot, not at the first session
// request. The executable itself is still probed when a browser is launched.
BrowserLaunchPlanResolver.Resolve(
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlaywrightOptions>>().Value,
    requireExecutable: false);

app.MapPost("/api/sessions", async (
    SessionRequest request,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    // Session options and browser mode are validated in the driver; report those
    // precondition failures as a client error carrying the actionable message
    // instead of an opaque 500.
    try
    {
        var entry = await registry.StartAsync(request, cancellationToken);
        return Results.Created($"/api/sessions/{entry.Session.Id}", new
        {
            SessionId = entry.Session.Id,
            entry.Session.CurrentUrl,
            entry.Options.BaseUrl,
            entry.StartedAt,
        });
    }
    catch (Exception exception) when (
        (exception is InvalidOperationException and not ObjectDisposedException)
        || exception is NotSupportedException)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapGet("/api/sessions/{id}/events", (
    string id,
    long? after,
    int? limit,
    SessionRegistry registry) =>
{
    var entry = registry.Get(id);
    if (entry is null)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    if (limit is <= 0)
    {
        return Results.BadRequest(new { message = "limit must be greater than zero" });
    }

    var events = entry.Session.ReadEvents(after ?? 0, limit ?? 200);
    return Results.Ok(new
    {
        items = events,
        dropped = entry.Session.DroppedEventCount,
    });
});

app.MapGet("/api/sessions/{id}/events/stream", (
    string id,
    long? after,
    HttpContext context,
    SessionRegistry registry,
    CancellationToken cancellationToken)
    => StreamSessionEventsAsync(id, after, context, registry, cancellationToken));

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
            DroppedEvents = entry.Session.DroppedEventCount,
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
        case "waitforresponse":
            {
                if (request.Method is null && request.Target is null && request.Status is null)
                {
                    return Results.BadRequest(new
                    {
                        message = "waitforresponse requires at least one of target (url pattern), method or status",
                    });
                }

                if (request.TimeoutMs is <= 0)
                {
                    return Results.BadRequest(new { message = "timeoutMs must be greater than zero" });
                }

                try
                {
                    var response = await session.WaitForResponseAsync(
                        new ResponseMatcher
                        {
                            Method = request.Method,
                            UrlPattern = request.Target,
                            Status = request.Status,
                            Timeout = request.TimeoutMs is { } timeoutMs
                                ? TimeSpan.FromMilliseconds(timeoutMs)
                                : null,
                            IncludeBody = request.IncludeBody ?? false,
                        },
                        cancellationToken);

                    return Results.Ok(new
                    {
                        response.RequestId,
                        response.Method,
                        response.Status,
                        response.Url,
                        response.DurationMs,
                        response.Body,
                    });
                }
                catch (TimeoutException exception)
                {
                    return Results.Json(
                        new { message = exception.Message },
                        statusCode: StatusCodes.Status408RequestTimeout);
                }
                catch (InvalidOperationException exception)
                {
                    // The session's event log closed while waiting, so waiting again is
                    // a conflict rather than a server fault.
                    return Results.Json(
                        new { message = exception.Message },
                        statusCode: StatusCodes.Status409Conflict);
                }
            }
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

// Streams one session's event log as SSE: replays the buffer, then follows live
// events, interleaving keepalive comments while idle and a final "event: closed"
// frame when the session's log completes. A pump task feeds a local channel so
// this handler stays the only writer of the response body.
static async Task StreamSessionEventsAsync(
    string id,
    long? after,
    HttpContext context,
    SessionRegistry registry,
    CancellationToken cancellationToken)
{
    const int keepAliveSeconds = 15;

    if (registry.Get(id) is not { } entry)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await SessionEventSse.WriteReadyAsync(context.Response, cancellationToken);

    var events = Channel.CreateBounded<BrowserEvent>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    var pump = Task.Run(
        async () =>
        {
            try
            {
                await foreach (var item in entry.Session.SubscribeEventsAsync(after ?? 0, cancellationToken))
                {
                    events.Writer.TryWrite(item);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or the host is shutting down.
            }
            finally
            {
                events.Writer.TryComplete();
            }
        },
        cancellationToken);

    try
    {
        var pending = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var keepAlive = Task.Delay(TimeSpan.FromSeconds(keepAliveSeconds), cancellationToken);

        while (true)
        {
            var finished = await Task.WhenAny(pending, keepAlive).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (finished == keepAlive)
            {
                await SessionEventSse.WriteKeepAliveAsync(context.Response, cancellationToken).ConfigureAwait(false);
                keepAlive = Task.Delay(TimeSpan.FromSeconds(keepAliveSeconds), cancellationToken);
                continue;
            }

            if (!await pending.ConfigureAwait(false))
            {
                break;
            }

            while (events.Reader.TryRead(out var item))
            {
                await SessionEventSse.WriteEventAsync(context.Response, item, cancellationToken).ConfigureAwait(false);
            }

            pending = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
        }

        await SessionEventSse.WriteClosedAsync(context.Response, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected or the host is shutting down; nothing to report.
    }
    catch (IOException)
    {
        // The client went away mid-write.
    }
    finally
    {
        await pump.ConfigureAwait(false);
    }
}
