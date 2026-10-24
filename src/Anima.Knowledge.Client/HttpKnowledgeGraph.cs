using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anima.Knowledge.Client;

/// <summary>
/// <see cref="IKnowledgeGraph"/> over the memgraph-compatible HTTP API, so an
/// agent can use the local <c>memgraph</c> service instead of the in-memory graph.
/// </summary>
public sealed class HttpKnowledgeGraph : IKnowledgeGraph, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _vocabularyGate = new(1, 1);
    private Vocabulary? _vocabulary;

    public HttpKnowledgeGraph(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public async Task<KnowledgeNode> CreateNodeAsync(
        NodeDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var kindId = vocabulary.KindId(draft.Kind);

        using var response = await _http.PostAsJsonAsync(
            "api/nodes",
            new
            {
                kind_id = kindId,
                name = draft.Name,
                description = draft.Description ?? string.Empty,
                content = draft.Content ?? string.Empty,
            },
            JsonOptions,
            cancellationToken);

        var node = await ReadAsync<NodeDto>(response, cancellationToken);
        return node.ToModel(draft.Kind);
    }

    public async Task<KnowledgeNode?> GetNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/nodes/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var node = await ReadAsync<NodeDto>(response, cancellationToken);
        return node.ToModel(vocabulary.KindName(node.KindId));
    }

    public async Task<NodePage> QueryNodesAsync(
        NodeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var path = BuildNodeQuery("api/nodes", query);
        var page = await _http.GetFromJsonAsync<NodeEnvelope>(path, JsonOptions, cancellationToken)
            ?? new NodeEnvelope([]);
        var items = page.Items ?? [];

        return new NodePage(
            items.Select(item => item.ToModel(vocabulary.KindName(item.KindId))).ToArray(),
            page.Total ?? items.Count,
            query.Limit,
            query.Offset);
    }

    public async Task<NodePage> SearchAsync(
        string text,
        NodeQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var effective = query ?? new NodeQuery();
        var path = BuildNodeQuery("api/search", effective, text);

        var envelope = await _http.GetFromJsonAsync<SearchEnvelope>(path, JsonOptions, cancellationToken)
            ?? new SearchEnvelope(null, null, null);

        var hits = envelope.Items
            ?? envelope.Segments?.SelectMany(segment => segment.Matches ?? []).ToList()
            ?? [];

        var nodes = hits.Select(hit => new KnowledgeNode
        {
            Id = hit.Id,
            Kind = vocabulary.KindName(hit.KindId),
            Name = hit.Name,
            Description = hit.Summary,
            Resolved = hit.Resolved,
        }).ToArray();

        return new NodePage(nodes, envelope.Total ?? nodes.Length, effective.Limit, effective.Offset);
    }

    public async Task<bool> DeleteNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/nodes/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<KnowledgeEdge> AddEdgeAsync(
        EdgeDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var vocabulary = await GetVocabularyAsync(cancellationToken);

        using var response = await _http.PostAsJsonAsync(
            "api/edges",
            new
            {
                source = draft.Source,
                destination = draft.Destination,
                edge_type_id = vocabulary.EdgeTypeId(draft.EdgeType),
            },
            JsonOptions,
            cancellationToken);

        var edge = await ReadAsync<EdgeDto>(response, cancellationToken);
        return edge.ToModel(draft.EdgeType);
    }

    public async Task<bool> RemoveEdgeAsync(
        long source,
        long destination,
        string edgeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeType);
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var path = $"api/edges?source={source}&destination={destination}&edge_type_id={vocabulary.EdgeTypeId(edgeType)}";

        using var response = await _http.DeleteAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<NeighborResult>> GetNeighborsAsync(
        NeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var direction = query.Direction switch
        {
            EdgeDirection.Outgoing => "outgoing",
            EdgeDirection.Incoming => "incoming",
            _ => "both",
        };

        var path = $"api/nodes/{query.NodeId}/neighbors?direction={direction}";
        if (!string.IsNullOrWhiteSpace(query.EdgeType))
        {
            path += $"&edge_type={Uri.EscapeDataString(query.EdgeType)}";
        }

        var envelope = await _http.GetFromJsonAsync<NeighborEnvelope>(path, JsonOptions, cancellationToken)
            ?? new NeighborEnvelope([]);

        return (envelope.Items ?? []).Select(item => new NeighborResult(
            item.Node.ToModel(vocabulary.KindName(item.Node.KindId)),
            item.Edge.ToModel(item.Edge.EdgeType ?? vocabulary.EdgeTypeName(item.Edge.EdgeTypeId)),
            string.Equals(item.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
                ? EdgeDirection.Outgoing
                : EdgeDirection.Incoming)).ToArray();
    }

    public async Task<IReadOnlyList<NodeKindDefinition>> GetKindsAsync(
        CancellationToken cancellationToken = default)
    {
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        return vocabulary.Kinds;
    }

    public async Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(
        CancellationToken cancellationToken = default)
    {
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        return vocabulary.EdgeTypes;
    }

    public async Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = await _http.GetFromJsonAsync<HealthDto>("api/graph-health", JsonOptions, cancellationToken);
        return health is null
            ? new GraphHealth(0, 0, 0, 0, 0)
            : new GraphHealth(
                health.NodeCount,
                health.EdgeCount,
                health.IsolatedNodes,
                health.UnanchoredWork,
                health.DuplicateEdgePairs);
    }

    public async Task<PlanResult> CreatePlanAsync(
        PlanDraft plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var tasks = plan.Tasks ?? [];
        var request = new
        {
            goal = plan.GoalId is null
                ? new
                {
                    name = plan.GoalName,
                    description = plan.GoalDescription ?? string.Empty,
                    content = plan.GoalContent ?? string.Empty,
                }
                : null,
            goal_id = plan.GoalId,
            anchor = plan.Anchor is null
                ? null
                : new { node_id = plan.Anchor.NodeId, edge_type = plan.Anchor.EdgeType },
            tasks = tasks.Select(task => new
            {
                key = task.Key,
                name = task.Name,
                description = task.Description ?? string.Empty,
                content = task.Content ?? string.Empty,
                parent = task.Parent,
                depends_on = task.DependsOn,
                affects = task.Affects,
            }),
        };

        using var response = await _http.PostAsJsonAsync("api/plans", request, JsonOptions, cancellationToken);
        var result = await ReadAsync<PlanEnvelope>(response, cancellationToken);
        var vocabulary = await GetVocabularyAsync(cancellationToken);
        var dependencies = tasks.ToDictionary(task => task.Key, task => task.DependsOn ?? [], StringComparer.Ordinal);

        return new PlanResult(
            result.Goal?.ToModel(vocabulary.KindName(result.Goal.KindId)),
            (result.Tasks ?? [])
                .Select(item => new PlanTaskResult(
                    item.Key,
                    item.Node.ToModel(vocabulary.KindName(item.Node.KindId)),
                    dependencies.GetValueOrDefault(item.Key) ?? []))
                .ToArray());
    }

    public async Task<KnowledgeNode> CreateTaskAsync(
        TaskDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        using var response = await _http.PostAsJsonAsync(
            "api/tasks",
            new
            {
                name = draft.Name,
                description = draft.Description ?? string.Empty,
                content = draft.Content ?? string.Empty,
                parent = draft.Parent,
                depends_on = draft.DependsOn,
                affects = draft.Affects,
            },
            JsonOptions,
            cancellationToken);

        var node = await ReadAsync<NodeDto>(response, cancellationToken);
        return node.ToModel(KnowledgeKinds.Task);
    }

    public async Task<KnowledgeNode> PatchTaskAsync(
        long id,
        TaskPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        using var response = await _http.PatchAsJsonAsync(
            $"api/tasks/{id}",
            new { name = patch.Name, description = patch.Description, content = patch.Content },
            JsonOptions,
            cancellationToken);

        var node = await ReadAsync<NodeDto>(response, cancellationToken);
        return node.ToModel(KnowledgeKinds.Task);
    }

    public void Dispose() => _vocabularyGate.Dispose();

    private static string BuildNodeQuery(
        string path,
        NodeQuery query,
        string? terms = null)
    {
        var parameters = new List<string>();
        if (terms is not null)
        {
            parameters.Add($"q={Uri.EscapeDataString(terms)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            parameters.Add($"kind={Uri.EscapeDataString(query.Kind)}");
        }

        parameters.Add($"scope={query.Scope.ToString().ToLowerInvariant()}");
        if (query.Within is { } within)
        {
            parameters.Add($"within={within}");
        }

        if (!string.IsNullOrWhiteSpace(query.Via))
        {
            parameters.Add($"via={Uri.EscapeDataString(query.Via)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Descend))
        {
            parameters.Add($"descend={Uri.EscapeDataString(query.Descend)}");
        }

        parameters.Add($"limit={query.Limit}");
        parameters.Add($"offset={query.Offset}");
        return $"{path}?{string.Join('&', parameters)}";
    }

    private async Task<Vocabulary> GetVocabularyAsync(CancellationToken cancellationToken)
    {
        if (_vocabulary is not null)
        {
            return _vocabulary;
        }

        await _vocabularyGate.WaitAsync(cancellationToken);
        try
        {
            if (_vocabulary is not null)
            {
                return _vocabulary;
            }

            var kinds = await _http.GetFromJsonAsync<KindEnvelope>("api/node-kinds", JsonOptions, cancellationToken)
                ?? new KindEnvelope([]);
            var edgeTypes = await _http.GetFromJsonAsync<EdgeTypeEnvelope>("api/edge-types", JsonOptions, cancellationToken)
                ?? new EdgeTypeEnvelope([]);

            _vocabulary = new Vocabulary(kinds.Items ?? [], edgeTypes.Items ?? []);
            return _vocabulary;
        }
        finally
        {
            _vocabularyGate.Release();
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new KnowledgeException("knowledge service returned an empty body");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new KnowledgeNotFoundException(body),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                new KnowledgeValidationException(body),
            _ => new KnowledgeException($"knowledge service returned {(int)response.StatusCode}: {body}"),
        };
    }

    private sealed record KindEnvelope(
        [property: JsonPropertyName("items")] List<KindDto>? Items);

    private sealed record KindDto(
        long Id,
        string Name,
        string? Description);

    private sealed record EdgeTypeDto(
        long Id,
        string Name,
        string? Description,
        bool Resolves,
        [property: JsonPropertyName("single_outgoing")] bool SingleOutgoing,
        [property: JsonPropertyName("source_kinds")] List<string>? SourceKinds,
        [property: JsonPropertyName("destination_kinds")] List<string>? DestinationKinds);

    private sealed record EdgeTypeEnvelope(
        [property: JsonPropertyName("items")] List<EdgeTypeDto>? Items);

    private sealed record NodeDto(
        long Id,
        [property: JsonPropertyName("kind_id")] long KindId,
        string? Kind,
        string Name,
        string? Description,
        string? Content,
        Dictionary<string, string>? Properties,
        bool? Resolved,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt)
    {
        public KnowledgeNode ToModel(string kindName) => new()
        {
            Id = Id,
            Kind = string.IsNullOrWhiteSpace(Kind) ? kindName : Kind,
            Name = Name,
            Description = Description ?? string.Empty,
            Content = Content ?? string.Empty,
            Properties = Properties ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Resolved = Resolved ?? false,
            CreatedAt = CreatedAt ?? default,
            UpdatedAt = UpdatedAt ?? default,
        };
    }

    private sealed record NodeEnvelope(
        [property: JsonPropertyName("items")] List<NodeDto>? Items,
        [property: JsonPropertyName("total")] int? Total)
    {
        public NodeEnvelope(List<NodeDto> items)
            : this(items, items.Count)
        {
        }
    }

    private sealed record EdgeDto(
        long Id,
        long Source,
        long Destination,
        [property: JsonPropertyName("edge_type_id")] long EdgeTypeId,
        [property: JsonPropertyName("edge_type")] string? EdgeType,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt)
    {
        public KnowledgeEdge ToModel(string edgeTypeName) => new()
        {
            Id = Id,
            Source = Source,
            Destination = Destination,
            EdgeType = string.IsNullOrWhiteSpace(EdgeType) ? edgeTypeName : EdgeType,
            CreatedAt = CreatedAt ?? default,
        };
    }

    private sealed record NeighborDto(
        string Direction,
        EdgeDto Edge,
        NodeDto Node);

    private sealed record NeighborEnvelope(
        [property: JsonPropertyName("items")] List<NeighborDto>? Items);

    private sealed record SearchHit(
        long Id,
        [property: JsonPropertyName("kind_id")] long KindId,
        string Name,
        string Summary,
        bool Resolved);

    private sealed record SearchSegment(
        [property: JsonPropertyName("matches")] List<SearchHit>? Matches);

    private sealed record SearchEnvelope(
        [property: JsonPropertyName("segments")] List<SearchSegment>? Segments,
        [property: JsonPropertyName("items")] List<SearchHit>? Items,
        [property: JsonPropertyName("total")] int? Total);

    private sealed record PlanTaskNode(
        string Key,
        NodeDto Node);

    private sealed record PlanEnvelope(
        [property: JsonPropertyName("goal")] NodeDto? Goal,
        [property: JsonPropertyName("tasks")] List<PlanTaskNode>? Tasks);

    private sealed record HealthDto(
        [property: JsonPropertyName("node_count")] int NodeCount,
        [property: JsonPropertyName("edge_count")] int EdgeCount,
        [property: JsonPropertyName("isolated_nodes")] int IsolatedNodes,
        [property: JsonPropertyName("unanchored_work")] int UnanchoredWork,
        [property: JsonPropertyName("duplicate_edge_pairs")] int DuplicateEdgePairs);

    private sealed class Vocabulary
    {
        private readonly Dictionary<string, long> _kindIds;
        private readonly Dictionary<long, string> _kindNames;
        private readonly Dictionary<string, long> _edgeTypeIds;
        private readonly Dictionary<long, EdgeTypeDefinition> _edgeTypesById;

        public Vocabulary(IReadOnlyList<KindDto> kinds, IReadOnlyList<EdgeTypeDto> edgeTypes)
        {
            _kindIds = kinds.ToDictionary(kind => kind.Name, kind => kind.Id, StringComparer.OrdinalIgnoreCase);
            _kindNames = kinds.ToDictionary(kind => kind.Id, kind => kind.Name);
            _edgeTypeIds = edgeTypes.ToDictionary(type => type.Name, type => type.Id, StringComparer.OrdinalIgnoreCase);
            _edgeTypesById = edgeTypes.ToDictionary(type => type.Id, type => new EdgeTypeDefinition(
                (int)type.Id,
                type.Name,
                type.Description ?? string.Empty,
                new HashSet<string>(type.SourceKinds ?? [], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(type.DestinationKinds ?? [], StringComparer.OrdinalIgnoreCase),
                type.Resolves,
                type.SingleOutgoing));

            Kinds = kinds
                .Select(kind => new NodeKindDefinition((int)kind.Id, kind.Name, kind.Description ?? string.Empty))
                .ToArray();
            EdgeTypes = _edgeTypesById.Values.ToArray();
        }

        public IReadOnlyList<NodeKindDefinition> Kinds { get; }

        public IReadOnlyList<EdgeTypeDefinition> EdgeTypes { get; }

        public long KindId(string kind) => _kindIds.TryGetValue(kind, out var id)
            ? id
            : throw new KnowledgeValidationException($"unknown node kind '{kind}'");

        public string KindName(long id) => _kindNames.GetValueOrDefault(id, $"kind-{id}");

        public long EdgeTypeId(string edgeType) => _edgeTypeIds.TryGetValue(edgeType, out var id)
            ? id
            : throw new KnowledgeValidationException($"unknown edge type '{edgeType}'");

        public string EdgeTypeName(long id) => _edgeTypesById.TryGetValue(id, out var definition)
            ? definition.Name
            : $"edge-{id}";
    }
}
