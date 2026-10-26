using System.Threading.Channels;
using Anima.Automation;
using Anima.Automation.Dsl;
using Anima.Automation.Playwright;
using Anima.Automation.Service;
using Anima.Automation.Service.Contracts;
using Anima.Hosting;

// Long-edge cap for captures taken through HTTP: above this, vision providers
// downscale anyway and text stops being legible, so bytes buy no information.
const int ScreenshotMaxHeight = 1568;

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

app.MapGet("/api/sessions/{id}/screenshot", async (
    string id,
    bool? fullPage,
    string? target,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    if (registry.Get(id) is not { } entry)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    BrowserScreenshot shot;

    try
    {
        shot = await entry.Session.TakeScreenshotAsync(
            new BrowserScreenshotOptions
            {
                FullPage = fullPage ?? false,
                Target = target,
                MaxHeight = ScreenshotMaxHeight,
                // Unique name: concurrent GETs must not race on one path, and the
                // file is deleted once its bytes are in hand.
                Name = $"get-{Guid.NewGuid():n}",
            },
            cancellationToken);
    }
    catch (TimeoutException exception)
    {
        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status408RequestTimeout);
    }

    try
    {
        var content = await File.ReadAllBytesAsync(shot.Path, cancellationToken);
        return Results.File(content, "image/png", $"session-{id}.png");
    }
    finally
    {
        TryDelete(shot.Path);
    }
});

app.MapPost("/api/sessions/{id}/actions", async (
    string id,
    ActionRequest request,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    if (registry.Get(id) is not { } entry)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    try
    {
        return await ExecuteActionAsync(id, entry.Session, request, cancellationToken);
    }
    catch (TimeoutException exception)
    {
        // Playwright reports its own waits as TimeoutException; a caller that ran
        // out of time is not a server fault.
        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status408RequestTimeout);
    }
    catch (Exception exception) when (
        (exception is InvalidOperationException and not ObjectDisposedException)
        || exception is ArgumentException
        || exception is NotSupportedException)
    {
        // Bad action input (missing field, unknown button, negative count) is a
        // client error and its message is the whole point.
        return Results.BadRequest(new { message = exception.Message });
    }
});

static async Task<IResult> ExecuteActionAsync(
    string id,
    IBrowserSession session,
    ActionRequest request,
    CancellationToken cancellationToken)
{
    switch (request.Action.ToLowerInvariant())
    {
        case "goto":
        case "navigate":
            await session.NavigateAsync(Require(request.Target), cancellationToken);
            break;
        case "click":
            await session.ClickAsync(
                Require(request.Target),
                new BrowserClickOptions
                {
                    // Value is the mouse button; Modifiers is its own field, so one
                    // cannot be mistaken for the other.
                    Button = request.Value,
                    ClickCount = request.Count is > 0 ? request.Count.Value : 1,
                    Modifiers = request.Modifiers,
                },
                cancellationToken);
            break;
        case "dblclick":
        case "doubleclick":
            await session.DoubleClickAsync(Require(request.Target), cancellationToken);
            break;
        case "type":
            await session.TypeAsync(
                Require(request.Target),
                Require(request.Value),
                request.DelayMs,
                cancellationToken);
            break;
        case "clear":
            await session.ClearAsync(Require(request.Target), cancellationToken);
            break;
        case "hover":
            await session.HoverAsync(Require(request.Target), cancellationToken);
            break;
        case "check":
            await session.CheckAsync(
                Require(request.Target),
                request.Checked ?? true,
                cancellationToken);
            break;
        case "scroll":
            {
                var deltaX = request.X ?? 0;
                var deltaY = request.Y ?? 0;

                if (deltaX == 0 && deltaY == 0)
                {
                    return Results.BadRequest(new { message = "scroll requires a non-zero x or y" });
                }

                await session.ScrollAsync(deltaX, deltaY, request.Target, cancellationToken);
                break;
            }
        case "scrollintoview":
            await session.ScrollIntoViewAsync(Require(request.Target), cancellationToken);
            break;
        case "dragto":
            await session.DragToAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "setinputfiles":
            await session.SetInputFilesAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "reload":
            await session.ReloadAsync(cancellationToken);
            break;
        case "back":
        case "goback":
            await session.GoBackAsync(cancellationToken);
            break;
        case "setviewport":
            await session.SetViewportAsync(
                request.Width ?? throw new InvalidOperationException("setviewport requires width"),
                request.Height ?? throw new InvalidOperationException("setviewport requires height"),
                cancellationToken);
            break;
        case "text":
            return Results.Ok(new { Output = await session.GetTextAsync(Require(request.Target), cancellationToken) });
        case "attribute":
            return Results.Ok(new
            {
                Output = await session.GetAttributeAsync(
                    Require(request.Target),
                    Require(request.Value),
                    cancellationToken),
            });
        case "value":
            return Results.Ok(new { Output = await session.GetValueAsync(Require(request.Target), cancellationToken) });
        case "texts":
            return Results.Ok(new { Items = await session.QueryAllTextAsync(Require(request.Target), cancellationToken) });
        case "count":
            return Results.Ok(new { Count = await session.CountAsync(Require(request.Target), cancellationToken) });
        case "html":
            return Results.Ok(new { Output = await session.GetHtmlAsync(Require(request.Target), cancellationToken) });
        case "aria":
            return Results.Ok(new { Output = await session.AriaSnapshotAsync(request.Target, cancellationToken) });
        case "isvisible":
            return Results.Ok(new { Value = await session.IsVisibleAsync(Require(request.Target), cancellationToken) });
        case "isenabled":
            return Results.Ok(new { Value = await session.IsEnabledAsync(Require(request.Target), cancellationToken) });
        case "ischecked":
            return Results.Ok(new { Value = await session.IsCheckedAsync(Require(request.Target), cancellationToken) });
        case "box":
            return Results.Ok(new { Box = await session.GetBoxAsync(Require(request.Target), cancellationToken) });
        case "waitforurl":
            await session.WaitForUrlAsync(Require(request.Target), request.TimeoutMs, cancellationToken);
            break;
        case "waitforcount":
            await session.WaitForCountAsync(
                Require(request.Target),
                request.Count ?? throw new InvalidOperationException("waitforcount requires count"),
                request.TimeoutMs,
                cancellationToken);
            break;
        case "waitforloadstate":
            await session.WaitForLoadStateAsync(
                (request.Value ?? "load").ToLowerInvariant() switch
                {
                    "networkidle" => BrowserLoadState.NetworkIdle,
                    "domcontentloaded" => BrowserLoadState.DomContentLoaded,
                    "load" => BrowserLoadState.Load,
                    _ => throw new InvalidOperationException(
                        $"Unknown load state '{request.Value}'. Use load, domcontentloaded or networkidle."),
                },
                cancellationToken);
            break;
        case "screenshot":
            {
                if (request.Height is <= 0)
                {
                    return Results.BadRequest(new { message = "height must be greater than zero" });
                }

                var fullPage = string.Equals(request.Value, "fullpage", StringComparison.OrdinalIgnoreCase);
                var shot = await session.TakeScreenshotAsync(
                    new BrowserScreenshotOptions
                    {
                        FullPage = fullPage,
                        Target = request.Target,
                        // Stable per session/view by default so repeated captures
                        // overwrite instead of filling the directory; Name opts into
                        // keeping distinct files.
                        Name = request.Name ?? StableCaptureName(id, fullPage, request.Target),
                        // Keep a full-page capture inside a vision budget by default.
                        MaxHeight = request.Height ?? ScreenshotMaxHeight,
                    },
                    cancellationToken);

                return Results.Ok(shot);
            }
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
}

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

// Bounded capture names: one file per session and view, so polling a screenshot
// does not grow the artifact directory without limit.
static string StableCaptureName(string sessionId, bool fullPage, string? target)
{
    var mode = target is not null ? "element" : fullPage ? "full" : "viewport";
    var suffix = target is null ? string.Empty : "-" + Slug(target);
    return $"session-{sessionId}-{mode}{suffix}";
}

static string Slug(string value)
{
    var builder = new System.Text.StringBuilder(Math.Min(value.Length, 24));

    foreach (var character in value)
    {
        if (builder.Length == 24)
        {
            break;
        }

        builder.Append(char.IsLetterOrDigit(character) ? character : '-');
    }

    return builder.ToString();
}

static void TryDelete(string path)
{
    try
    {
        File.Delete(path);
    }
    catch (IOException)
    {
        // Best effort: the capture is already delivered.
    }
    catch (UnauthorizedAccessException)
    {
        // Same: a leftover file is not worth failing the response.
    }
}

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
