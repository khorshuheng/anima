using System.Globalization;
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

// A batch runs sequentially and each step may wait, so bound it the way a script is
// bounded; anything larger belongs in a script or several batches.
const int MaxBatchSteps = 200;

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
        runner.Format,
    });
});

app.MapPost("/api/scripts/validate", (ValidateScriptRequest request, IScriptRunner runner) =>
{
    var result = runner.Validate(request.Source);
    return Results.Ok(new
    {
        result.IsValid,
        result.Diagnostics,
        runner.Format,
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
        byte[] content = await File.ReadAllBytesAsync(shot.Path, cancellationToken);
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
        var outcome = await WithTimeoutAsync(
            entry.Session,
            request.TimeoutMs,
            () => ExecuteActionAsync(id, entry.Session, request, cancellationToken));

        return outcome.Payload is null
            ? Results.Ok(new { Url = entry.Session.CurrentUrl })
            : Results.Ok(outcome.Payload);
    }
    catch (TimeoutException exception)
    {
        // Playwright reports its own waits as TimeoutException; a caller that ran
        // out of time is not a server fault.
        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status408RequestTimeout);
    }
    catch (SessionClosedException exception)
    {
        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status409Conflict);
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

static async Task<ActionOutcome> ExecuteActionAsync(
    string id,
    IBrowserSession session,
    ActionRequest request,
    CancellationToken cancellationToken,
    int? stepIndex = null)
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
                    Position = ToBrowserPosition(request.Position),
                },
                cancellationToken);
            break;
        case "dblclick":
        case "doubleclick":
            await session.DoubleClickAsync(
                Require(request.Target),
                new BrowserClickOptions
                {
                    Button = request.Value,
                    Modifiers = request.Modifiers,
                    Position = ToBrowserPosition(request.Position),
                },
                cancellationToken);
            break;
        case "type":
            await session.TypeAsync(
                Require(request.Target),
                Require(request.Value),
                new BrowserTypeOptions
                {
                    DelayMs = request.DelayMs,
                    ClearFirst = request.Clear ?? false,
                },
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
                int deltaX = request.X ?? 0;
                int deltaY = request.Y ?? 0;

                if (deltaX == 0 && deltaY == 0)
                {
                    throw new InvalidOperationException("scroll requires a non-zero x or y");
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
            {
                string? text = await session.GetTextAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Output = text }, text);
            }
        case "attribute":
            {
                string? attribute = await session.GetAttributeAsync(
                    Require(request.Target),
                    Require(request.Value),
                    cancellationToken);

                return new ActionOutcome(new { Output = attribute }, attribute);
            }
        case "value":
            {
                string? inputValue = await session.GetValueAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Output = inputValue }, inputValue);
            }
        case "texts":
            {
                var texts = await session.QueryAllTextAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Items = texts }, string.Join(" | ", texts));
            }
        case "count":
            {
                int count = await session.CountAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Count = count }, count.ToString(CultureInfo.InvariantCulture));
            }
        case "html":
            {
                string? html = await session.GetHtmlAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Output = html }, html);
            }
        case "aria":
            {
                string snapshot = await session.AriaSnapshotAsync(request.Target, cancellationToken);
                return new ActionOutcome(new { Output = snapshot }, snapshot);
            }
        case "isvisible":
            {
                bool visible = await session.IsVisibleAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Value = visible }, visible.ToString());
            }
        case "isenabled":
            {
                bool enabled = await session.IsEnabledAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Value = enabled }, enabled.ToString());
            }
        case "ischecked":
            {
                bool checkedState = await session.IsCheckedAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Value = checkedState }, checkedState.ToString());
            }
        case "box":
            {
                var box = await session.GetBoxAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Box = box }, box?.ToString());
            }
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
        case "fillform":
            {
                if (request.Fields is not { Count: > 0 } fields)
                {
                    throw new InvalidOperationException("fillform requires a non-empty fields map");
                }

                var results = await session.FillFormAsync(ToFormFields(fields), cancellationToken);

                return new ActionOutcome(new { Fields = results }, $"{results.Count} field(s) filled");
            }
        case "formstate":
            {
                var state = await session.GetFormStateAsync(request.Target, cancellationToken);

                // A flat key -> value map makes diffing against source data a one-call
                // check. Values keep their kind (bool for toggles, an array for several
                // choices) and a repeated key - a radio group, typically - reports every
                // entry instead of silently keeping the last.
                var values = state
                    .Where(field => field.Key.Length > 0)
                    .GroupBy(field => field.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count() == 1
                            ? FlatValue(group.First())
                            : group.Select(FlatValue).ToArray(),
                        StringComparer.Ordinal);

                return new ActionOutcome(new { Items = state, Values = values }, $"{state.Count} control(s)");
            }
        case "screenshot":
            {
                if (request.Height is <= 0)
                {
                    throw new InvalidOperationException("height must be greater than zero");
                }

                bool fullPage = string.Equals(request.Value, "fullpage", StringComparison.OrdinalIgnoreCase);
                var shot = await session.TakeScreenshotAsync(
                    new BrowserScreenshotOptions
                    {
                        FullPage = fullPage,
                        Target = request.Target,
                        // Stable per session/view by default so repeated captures
                        // overwrite instead of filling the directory; Name opts into
                        // keeping distinct files.
                        Name = request.Name
                            ?? StableCaptureName(id, fullPage, request.Target)
                            + (stepIndex is { } index ? $"-{index}" : string.Empty),
                        // Keep a full-page capture inside a vision budget by default.
                        MaxHeight = request.Height ?? ScreenshotMaxHeight,
                    },
                    cancellationToken);

                return new ActionOutcome(shot, shot.Path);
            }
        case "fill":
            await session.FillAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "press":
            if (string.IsNullOrWhiteSpace(request.Target))
            {
                // No usable target: press on the page, so Escape closes a modal.
                await session.PressKeyAsync(Require(request.Value), cancellationToken);
                break;
            }

            await session.PressAsync(request.Target, Require(request.Value), cancellationToken);
            break;
        case "select":
            if (request.Values is { Length: > 0 })
            {
                await session.SelectAsync(Require(request.Target), request.Values, cancellationToken);
                break;
            }

            await session.SelectAsync(Require(request.Target), Require(request.Value), cancellationToken);
            break;
        case "waitfor":
            await session.WaitForAsync(
                Require(request.Target),
                ParseWaitState(request.State),
                cancellationToken);
            break;
        case "eval":
            {
                string value = await session.EvaluateAsync(Require(request.Target), cancellationToken);
                return new ActionOutcome(new { Output = value }, value);
            }
        case "waitforresponse":
            {
                if (request.Method is null && request.Target is null && request.Status is null)
                {
                    throw new InvalidOperationException(
                        "waitforresponse requires at least one of target (url pattern), method or status");
                }

                if (request.TimeoutMs is <= 0)
                {
                    throw new InvalidOperationException("timeoutMs must be greater than zero");
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

                    return new ActionOutcome(
                        new
                        {
                            response.RequestId,
                            response.Method,
                            response.Status,
                            response.Url,
                            response.DurationMs,
                            response.Body,
                        },
                        $"{response.Method} {response.Status} {response.Url}");
                }
                catch (InvalidOperationException exception)
                {
                    // The session's event log closed while waiting: retrying cannot
                    // help, so it is a conflict rather than a bad request.
                    throw new SessionClosedException(exception.Message);
                }
            }
        default:
            throw new InvalidOperationException($"unknown action '{request.Action}'");
    }

    return new ActionOutcome(null, session.CurrentUrl);
}

app.MapPost("/api/sessions/{id}/actions/batch", async (
    string id,
    HttpRequest http,
    SessionRegistry registry,
    CancellationToken cancellationToken) =>
{
    // Accepts a bare array of actions or {"steps":[...],"stopOnError":false}.
    var parsed = await BatchBodyReader.ReadAsync(http, cancellationToken);

    if (parsed.Error is { } bodyError)
    {
        return Results.BadRequest(new { message = bodyError });
    }

    if (registry.Get(id) is not { } entry)
    {
        return Results.NotFound(new { message = $"session '{id}' does not exist" });
    }

    if (parsed.Steps is not { Count: > 0 } request)
    {
        return Results.BadRequest(new { message = "batch requires at least one step" });
    }

    if (request.Count > MaxBatchSteps)
    {
        return Results.BadRequest(new
        {
            message = $"batch accepts at most {MaxBatchSteps} steps, got {request.Count}",
        });
    }

    for (int index = 0; index < request.Count; index++)
    {
        if (request[index] is null)
        {
            return Results.BadRequest(new { message = $"step {index} is null" });
        }
    }

    bool stopOnError = parsed.StopOnError ?? true;
    var results = new List<object>(request.Count);
    bool success = true;
    string? error = null;

    for (int index = 0; index < request.Count; index++)
    {
        var step = request[index];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var outcome = await WithTimeoutAsync(
                entry.Session,
                step.TimeoutMs,
                () => ExecuteActionAsync(
                    id,
                    entry.Session,
                    step,
                    cancellationToken,
                    // Distinguish captures taken inside one batch.
                    stepIndex: index));

            results.Add(new
            {
                Index = index,
                step.Action,
                Success = true,
                DurationMs = stopwatch.ElapsedMilliseconds,
                outcome.Output,
                Error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // Same result shape as a script run: the step that failed carries the
            // message, and the remaining steps are simply absent.
            results.Add(new
            {
                Index = index,
                step.Action,
                Success = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Output = (string?)null,
                Error = exception.Message,
            });

            success = false;
            error = exception.Message;

            if (stopOnError)
            {
                break;
            }
        }
    }

    return Results.Ok(new
    {
        Success = success,
        Steps = results,
        Error = error,
    });
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

    string result = await cdp.SendAsync(request.Method, request.ParametersJson, cancellationToken);
    return Results.Ok(new { Result = result });
});

app.Run();

// Per-action timeout: narrow the session's default for the duration of one action,
// so every verb (including composite waits) honours timeoutMs without threading a
// parameter through each method.
static async Task<T> WithTimeoutAsync<T>(IBrowserSession session, int? timeoutMs, Func<Task<T>> action)
{
    if (timeoutMs is not { } requested)
    {
        return await action();
    }

    if (requested <= 0)
    {
        throw new InvalidOperationException("timeoutMs must be greater than zero");
    }

    double? previous = session.TimeoutOverrideMs;
    session.TimeoutOverrideMs = requested;

    try
    {
        return await action();
    }
    finally
    {
        session.TimeoutOverrideMs = previous;
    }
}

// A field's value in the diff-friendly form: booleans for toggles, a single string
// for one choice, an array for several, otherwise the raw value.
static object? FlatValue(FormFieldState field)
{
    if (field.Toggle is { } toggle)
    {
        return toggle;
    }

    if (field.Control is "select-multiple" or "antd-select")
    {
        return field.Choices is { Count: 1 } single ? single[0] : field.Choices;
    }

    return field.Value;
}

static Dictionary<string, FormFieldValue> ToFormFields(
    Dictionary<string, System.Text.Json.JsonElement> fields)
{
    var converted = new Dictionary<string, FormFieldValue>(fields.Count, StringComparer.Ordinal);

    foreach (var (selector, value) in fields)
    {
        converted[selector] = value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => FormFieldValue.FromText(value.GetString() ?? string.Empty),
            System.Text.Json.JsonValueKind.True => FormFieldValue.FromToggle(true),
            System.Text.Json.JsonValueKind.False => FormFieldValue.FromToggle(false),
            System.Text.Json.JsonValueKind.Number => FormFieldValue.FromText(value.GetRawText()),
            System.Text.Json.JsonValueKind.Array => FormFieldValue.FromChoices(
                value.EnumerateArray().Select(item => item.ValueKind == System.Text.Json.JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : item.GetRawText())),
            _ => throw new InvalidOperationException(
                $"fillform value for '{selector}' must be a string, boolean or array of strings"),
        };
    }

    return converted;
}

static BrowserPosition? ToBrowserPosition(ActionPosition? position)
    => position is null ? null : new BrowserPosition(position.X, position.Y);

static WaitState ParseWaitState(string? state) => (state ?? "visible").ToLowerInvariant() switch
{
    "visible" => WaitState.Visible,
    "hidden" => WaitState.Hidden,
    "attached" => WaitState.Attached,
    "detached" => WaitState.Detached,
    _ => throw new InvalidOperationException(
        $"Unknown wait state '{state}'. Use visible, hidden, attached or detached."),
};

// Bounded capture names: one file per session and view, so polling a screenshot
// does not grow the artifact directory without limit.
static string StableCaptureName(string sessionId, bool fullPage, string? target)
{
    string mode = target is not null ? "element" : fullPage ? "full" : "viewport";
    string suffix = target is null ? string.Empty : "-" + Slug(target);
    return $"session-{sessionId}-{mode}{suffix}";
}

static string Slug(string value)
{
    var builder = new System.Text.StringBuilder(Math.Min(value.Length, 24));

    foreach (char character in value)
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
