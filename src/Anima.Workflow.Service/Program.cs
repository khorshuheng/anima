using System.Text.Json;
using System.Text.Json.Serialization;
using Anima.Bus;
using Anima.Bus.Client;
using Anima.Hosting;
using Anima.Knowledge;
using Anima.Knowledge.Client;
using Anima.Workflow;
using Anima.Workflow.Service.Contracts;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("workflow", AnimaPorts.Workflow);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var knowledgeTransport = builder.Configuration["Anima:Knowledge:Transport"] ?? "Http";
if (string.Equals(knowledgeTransport, "Http", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddKnowledgeClient(options =>
    {
        options.BaseUrl = builder.Configuration["Anima:Knowledge:BaseUrl"] ?? "http://127.0.0.1:7411";
        options.SessionId = "anima-workflow";
    });
}
else
{
    builder.Services.AddInMemoryKnowledgeGraph();
}

var busTransport = builder.Configuration["Anima:Bus:Transport"] ?? "Http";
if (string.Equals(busTransport, "Http", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpBus(options =>
    {
        options.BaseUrl = builder.Configuration["Anima:Bus:BaseUrl"] ?? "http://127.0.0.1:7412";
    });
}
else
{
    builder.Services.AddInProcBus();
}

builder.Services.AddWorkflowCore();
builder.Services.Configure<WorkflowOptions>(builder.Configuration.GetSection("Anima:Workflow"));

var app = builder.Build();
app.UseAnimaServiceDefaults("workflow");

var uiRoot = Path.Combine(RepoLayout.Root, "ui", "dist");
if (Directory.Exists(uiRoot))
{
    var uiFiles = new PhysicalFileProvider(uiRoot);
    app.Use(async (context, next) =>
    {
        if (context.Request.Path == "/ui")
        {
            context.Response.Redirect("/ui/");
            return;
        }

        await next(context);
    });
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = uiFiles, RequestPath = "/ui" });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = uiFiles, RequestPath = "/ui" });
}
else
{
    app.Logger.LogWarning("UI build not found at {UiRoot}; run 'make ui-build'.", uiRoot);
}

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (WorkflowNotFoundException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message });
    }
    catch (WorkflowValidationException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message });
    }
    catch (KnowledgeNotFoundException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message });
    }
    catch (KnowledgeValidationException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await context.Response.WriteAsJsonAsync(new { message = exception.Message });
    }
});

app.MapPost("/api/workflows", async (
    WorkflowStart start,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.CreateAsync(start, cancellationToken);
    return Results.Created($"/api/workflows/{state.Id}", state);
});

app.MapGet("/api/workflows", async (IWorkflowStore store, CancellationToken cancellationToken) =>
    Results.Ok(new { items = await store.ListAsync(cancellationToken) }));

app.MapGet("/api/workflows/{id:long}", async (
    long id,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
{
    var state = await store.GetAsync(id, cancellationToken);
    return state is null
        ? Results.NotFound(new { message = $"workflow {id} does not exist" })
        : Results.Ok(state);
});

app.MapGet("/api/workflows/{id:long}/context", async (
    long id,
    string? taskKey,
    IWorkflowStore store,
    CancellationToken cancellationToken) => Results.Ok(
    await store.GetContextAsync(id, taskKey, cancellationToken)));

app.MapGet("/api/workflows/{id:long}/questions", async (
    long id,
    bool? open,
    IWorkflowStore store,
    CancellationToken cancellationToken) => Results.Ok(new
    {
        items = await store.GetQuestionsAsync(id, open ?? true, cancellationToken),
    }));

app.MapPost("/api/workflows/{id:long}/questions", async (
    long id,
    QuestionsRequest request,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
{
    var added = await store.AddQuestionsAsync(id, request.Questions, cancellationToken);
    var state = await store.GetAsync(id, cancellationToken)
        ?? throw new WorkflowNotFoundException($"workflow {id} does not exist");
    var open = await store.GetQuestionsAsync(id, openOnly: true, cancellationToken);
    return Results.Ok(new QuestionsResponse(added, open, state.Status));
});

app.MapPost("/api/workflows/{id:long}/answers", async (
    long id,
    AnswersRequest request,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
{
    var added = await store.AddAnswersAsync(id, request.Answers, cancellationToken);
    var state = await store.GetAsync(id, cancellationToken)
        ?? throw new WorkflowNotFoundException($"workflow {id} does not exist");
    var open = await store.GetQuestionsAsync(id, openOnly: true, cancellationToken);
    return Results.Ok(new AnswersResponse(added, open, state.Status));
});

app.MapGet("/api/workflows/{id:long}/answers", async (
    long id,
    long? since,
    IWorkflowStore store,
    CancellationToken cancellationToken) => Results.Ok(new
    {
        items = await store.GetAnswersAsync(id, since ?? 0, cancellationToken),
    }));

app.MapGet("/api/workflows/{id:long}/spec", async (
    long id,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
{
    var spec = await store.GetSpecAsync(id, cancellationToken);
    return spec is null
        ? Results.NotFound(new { message = $"workflow {id} has no spec yet" })
        : Results.Ok(spec);
});

app.MapPost("/api/workflows/{id:long}/spec", async (
    long id,
    SpecInput spec,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.WriteSpecAsync(id, spec, cancellationToken)));

app.MapGet("/api/workflows/{id:long}/assignments", async (
    long id,
    IWorkflowStore store,
    CancellationToken cancellationToken) => Results.Ok(new
    {
        items = await store.GetAssignmentsAsync(id, cancellationToken),
    }));

app.MapPost("/api/workflows/{id:long}/assignments", async (
    long id,
    AssignmentInput assignment,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.RegisterAssignmentAsync(id, assignment, cancellationToken)));

app.MapDelete("/api/workflows/{id:long}/assignments/{taskKey}", async (
    long id,
    string taskKey,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
    await store.RemoveAssignmentAsync(id, taskKey, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound(new { message = $"no assignment for task '{taskKey}'" }));

app.MapGet("/api/workflows/{id:long}/results", async (
    long id,
    IWorkflowStore store,
    CancellationToken cancellationToken) => Results.Ok(new
    {
        items = await store.GetResultsAsync(id, cancellationToken),
    }));

app.MapPost("/api/workflows/{id:long}/results", async (
    long id,
    ResultInput result,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.AddResultAsync(id, result, cancellationToken)));

app.MapGet("/api/workflows/{id:long}/timeline", async (
    long id,
    IWorkflowStore store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.GetTimelineAsync(id, cancellationToken)));

app.MapPost("/api/workflows/{id:long}/dispatch", async (
    long id,
    DispatchRequest request,
    IWorkflowDispatcher dispatcher,
    CancellationToken cancellationToken) =>
    Results.Ok(await dispatcher.DispatchAsync(id, request, cancellationToken)));

app.Run();
