using System.Text.Json;
using Anima.Knowledge;

namespace Anima.Agent;

/// <summary>Writes a durable memory node (or any node kind) into the knowledge graph.</summary>
public sealed class KnowledgeRememberTool : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IKnowledgeGraph _graph;

    public KnowledgeRememberTool(IKnowledgeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    public string Name => "knowledge.remember";

    public string Description =>
        "Create a knowledge node. Arguments: { kind, name, description?, content?, properties? }.";

    public async Task<string> InvokeAsync(
        string argumentsJson,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = JsonSerializer.Deserialize<RememberArguments>(argumentsJson, JsonOptions)
            ?? throw new InvalidOperationException("knowledge.remember requires a JSON object");

        var node = await _graph.CreateNodeAsync(
            new NodeDraft(
                arguments.Kind ?? KnowledgeKinds.Memory,
                arguments.Name ?? throw new InvalidOperationException("name is required"),
                arguments.Description,
                arguments.Content,
                arguments.Properties),
            cancellationToken);

        return JsonSerializer.Serialize(new { node.Id, node.Kind, node.Name }, JsonOptions);
    }

    private sealed record RememberArguments(
        string? Kind,
        string? Name,
        string? Description,
        string? Content,
        Dictionary<string, string>? Properties);
}

/// <summary>Searches the knowledge graph and returns compact hits.</summary>
public sealed class KnowledgeSearchTool : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IKnowledgeGraph _graph;

    public KnowledgeSearchTool(IKnowledgeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    public string Name => "knowledge.search";

    public string Description =>
        "Search tasks and memories. Arguments: { query, kind?, scope? }.";

    public async Task<string> InvokeAsync(
        string argumentsJson,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = JsonSerializer.Deserialize<SearchArguments>(argumentsJson, JsonOptions)
            ?? throw new InvalidOperationException("knowledge.search requires a JSON object");

        if (string.IsNullOrWhiteSpace(arguments.Query))
        {
            throw new InvalidOperationException("query is required");
        }

        var page = await _graph.SearchAsync(
            arguments.Query,
            new NodeQuery
            {
                Kind = arguments.Kind,
                Scope = Enum.TryParse<KnowledgeScope>(arguments.Scope, ignoreCase: true, out var scope)
                    ? scope
                    : KnowledgeScope.Active,
            },
            cancellationToken);

        return JsonSerializer.Serialize(
            new
            {
                page.Total,
                items = page.Items.Select(node => new
                {
                    node.Id,
                    node.Kind,
                    node.Name,
                    node.Description,
                    node.Resolved,
                }),
            },
            JsonOptions);
    }

    private sealed record SearchArguments(string? Query, string? Kind, string? Scope);
}
