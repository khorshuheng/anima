using System.Text.Json;
using Anima.Hosting;
using Anima.Knowledge;
using Anima.Knowledge.Service;
using Anima.Knowledge.Service.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("knowledge", AnimaPorts.Knowledge);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // memgraph-compatible wire shape: kind_id, edge_type_id, created_at, ...
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = null;
});
builder.Services.AddInMemoryKnowledgeGraph();
builder.Services.Configure<KnowledgeOptions>(builder.Configuration.GetSection("Anima:Knowledge"));

var app = builder.Build();
app.UseAnimaServiceDefaults("knowledge");

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
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

app.MapGet("/api/node-kinds", async (IKnowledgeGraph graph, CancellationToken cancellationToken) =>
{
    var kinds = await graph.GetKindsAsync(cancellationToken);
    return Results.Ok(new { items = kinds });
});

app.MapGet("/api/edge-types", async (IKnowledgeGraph graph, CancellationToken cancellationToken) =>
{
    var edgeTypes = await graph.GetEdgeTypesAsync(cancellationToken);
    return Results.Ok(new
    {
        items = edgeTypes.Select(type => new
        {
            type.Id,
            type.Name,
            type.Description,
            type.Resolves,
            type.SingleOutgoing,
            SourceKinds = type.SourceKinds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            DestinationKinds = type.DestinationKinds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        }),
    });
});

app.MapGet("/api/nodes", async (
    string? kind,
    long? kindId,
    string? scope,
    long? within,
    string? via,
    string? descend,
    int? limit,
    int? offset,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var kindName = kind;
    if (kindName is null && kindId is { } id)
    {
        var kinds = await graph.GetKindsAsync(cancellationToken);
        kindName = kinds.FirstOrDefault(candidate => candidate.Id == id)?.Name;
    }

    var page = await graph.QueryNodesAsync(
        new NodeQuery
        {
            Kind = kindName,
            Scope = KnowledgeEndpointMappings.ParseScope(scope),
            Within = within,
            Via = via,
            Descend = descend,
            Limit = limit ?? 100,
            Offset = offset ?? 0,
        },
        cancellationToken);

    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Ok(new
    {
        items = page.Items.Select(node => node.ToResponse(vocabulary.Kinds)),
        page.Total,
        page.Limit,
        page.Offset,
    });
});

app.MapPost("/api/nodes", async (
    CreateNodeRequest request,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var kind = request.Kind;
    if (kind is null && request.KindId is { } kindId)
    {
        var kinds = await graph.GetKindsAsync(cancellationToken);
        kind = kinds.FirstOrDefault(candidate => candidate.Id == kindId)?.Name
            ?? throw new KnowledgeValidationException($"unknown node kind id '{kindId}'");
    }

    if (kind is null)
    {
        throw new KnowledgeValidationException("kind or kind_id is required");
    }

    var node = await graph.CreateNodeAsync(
        new NodeDraft(kind, request.Name, request.Description, request.Content, request.Properties),
        cancellationToken);

    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Created($"/api/nodes/{node.Id}", node.ToResponse(vocabulary.Kinds));
});

app.MapGet("/api/nodes/{id:long}", async (
    long id,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var node = await graph.GetNodeAsync(id, cancellationToken)
        ?? throw new KnowledgeNotFoundException($"node {id} does not exist");
    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Ok(node.ToResponse(vocabulary.Kinds));
});

app.MapDelete("/api/nodes/{id:long}", async (
    long id,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var deleted = await graph.DeleteNodeAsync(id, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound(new { message = $"node {id} does not exist" });
});

app.MapGet("/api/nodes/{id:long}/neighbors", async (
    long id,
    string? direction,
    string? edgeType,
    long? edgeTypeId,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var typeName = edgeType;
    if (typeName is null && edgeTypeId is { } typeId)
    {
        var edgeTypes = await graph.GetEdgeTypesAsync(cancellationToken);
        typeName = edgeTypes.FirstOrDefault(candidate => candidate.Id == typeId)?.Name
            ?? throw new KnowledgeValidationException($"unknown edge type id '{typeId}'");
    }

    var neighbors = await graph.GetNeighborsAsync(
        new NeighborQuery(id, KnowledgeEndpointMappings.ParseDirection(direction), typeName),
        cancellationToken);

    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Ok(new
    {
        items = neighbors.Select(neighbor => new
        {
            Direction = neighbor.Direction.ToString().ToLowerInvariant(),
            Edge = neighbor.Edge.ToResponse(vocabulary.EdgeTypes),
            Node = neighbor.Node.ToResponse(vocabulary.Kinds),
        }),
    });
});

app.MapPost("/api/edges", async (
    CreateEdgeRequest request,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var typeName = request.EdgeType;
    if (typeName is null && request.EdgeTypeId is { } typeId)
    {
        var edgeTypes = await graph.GetEdgeTypesAsync(cancellationToken);
        typeName = edgeTypes.FirstOrDefault(candidate => candidate.Id == typeId)?.Name
            ?? throw new KnowledgeValidationException($"unknown edge type id '{typeId}'");
    }

    if (typeName is null)
    {
        throw new KnowledgeValidationException("edge_type or edge_type_id is required");
    }

    var edge = await graph.AddEdgeAsync(
        new EdgeDraft(request.Source, request.Destination, typeName),
        cancellationToken);

    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Created("/api/edges", edge.ToResponse(vocabulary.EdgeTypes));
});

app.MapDelete("/api/edges", async (
    long source,
    long destination,
    string? edgeType,
    long? edgeTypeId,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var typeName = edgeType;
    if (typeName is null && edgeTypeId is { } typeId)
    {
        var edgeTypes = await graph.GetEdgeTypesAsync(cancellationToken);
        typeName = edgeTypes.FirstOrDefault(candidate => candidate.Id == typeId)?.Name
            ?? throw new KnowledgeValidationException($"unknown edge type id '{typeId}'");
    }

    if (typeName is null)
    {
        throw new KnowledgeValidationException("edge_type or edge_type_id is required");
    }

    var removed = await graph.RemoveEdgeAsync(source, destination, typeName, cancellationToken);
    return removed ? Results.NoContent() : Results.NotFound(new { message = "edge does not exist" });
});

app.MapGet("/api/search", async (
    string q,
    string? scope,
    long? within,
    string? via,
    string? descend,
    int? limit,
    int? offset,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var query = new NodeQuery
    {
        Scope = KnowledgeEndpointMappings.ParseScope(scope),
        Within = within,
        Via = via,
        Descend = descend,
        Limit = limit ?? 100,
        Offset = offset ?? 0,
    };

    var page = await graph.SearchAsync(q, query, cancellationToken);
    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Ok(new
    {
        items = page.Items.Select(node => node.ToResponse(vocabulary.Kinds)),
        page.Total,
        page.Limit,
        page.Offset,
        segments = Array.Empty<object>(),
    });
});

app.MapPost("/api/tasks", async (
    CreateTaskRequest request,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var task = await graph.CreateTaskAsync(
        new TaskDraft(
            request.Name,
            request.Description,
            request.Content,
            request.Parent,
            request.DependsOn,
            request.Affects),
        cancellationToken);

    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Created($"/api/nodes/{task.Id}", task.ToResponse(vocabulary.Kinds));
});

app.MapPatch("/api/tasks/{id:long}", async (
    long id,
    PatchTaskRequest request,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    var task = await graph.PatchTaskAsync(
        id,
        new TaskPatch(request.Name, request.Description, request.Content),
        cancellationToken);

    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Ok(task.ToResponse(vocabulary.Kinds));
});

app.MapPost("/api/plans", async (
    PlanRequest request,
    IKnowledgeGraph graph,
    CancellationToken cancellationToken) =>
{
    if (request.Goal is null && request.GoalId is null)
    {
        throw new KnowledgeValidationException("exactly one of goal or goal_id is required");
    }

    if (request.Goal is not null && request.GoalId is not null)
    {
        throw new KnowledgeValidationException("goal and goal_id are mutually exclusive");
    }

    if (request.Goal is not null && request.Anchor is null)
    {
        throw new KnowledgeValidationException("anchor is required when a plan creates a goal");
    }

    var plan = new PlanDraft(
        request.Goal?.Name ?? string.Empty,
        request.Goal?.Description,
        request.Goal?.Content,
        request.Anchor is null ? null : new PlanAnchor(request.Anchor.NodeId, request.Anchor.EdgeType),
        request.GoalId,
        (request.Tasks ?? [])
            .Select(task => new PlanTaskDraft(
                task.Key,
                task.Name,
                task.Description,
                task.Content,
                task.Properties,
                task.Parent,
                task.DependsOn,
                task.Affects))
            .ToArray());

    var result = await graph.CreatePlanAsync(plan, cancellationToken);
    var vocabulary = await graph.VocabularyAsync(cancellationToken);
    return Results.Created("/api/plans", new
    {
        goal = result.Goal?.ToResponse(vocabulary.Kinds),
        tasks = result.Tasks.Select(task => new
        {
            task.Key,
            task.DependsOn,
            Node = task.Node.ToResponse(vocabulary.Kinds),
        }),
    });
});

app.MapGet("/api/graph-health", async (IKnowledgeGraph graph, CancellationToken cancellationToken) =>
{
    var health = await graph.GetHealthAsync(cancellationToken);
    return Results.Ok(health);
});

app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
{
    session = context.Request.Headers["X-Session-Id"].FirstOrDefault() ?? "anonymous",
}));

app.Run();
