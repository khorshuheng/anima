using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge;

/// <summary>
/// Reference implementation of the task/memory graph. It enforces edge type
/// declarations the same way the memgraph service does, derives "resolved" from
/// resolving edges, and validates plan wiring before writing anything.
/// </summary>
public sealed class InMemoryKnowledgeGraph : IKnowledgeGraph
{
    private readonly object _gate = new();
    private readonly KnowledgeOptions _options;
    private readonly ILogger<InMemoryKnowledgeGraph> _logger;
    private readonly Dictionary<long, NodeState> _nodes = [];
    private readonly Dictionary<long, EdgeState> _edges = [];
    private readonly Dictionary<string, NodeKindDefinition> _kinds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EdgeTypeDefinition> _edgeTypes =
        new(StringComparer.OrdinalIgnoreCase);

    private long _nextNodeId;
    private long _nextEdgeId;

    public InMemoryKnowledgeGraph(IOptions<KnowledgeOptions> options, ILogger<InMemoryKnowledgeGraph> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;

        foreach (var kind in KnowledgeKinds.Definitions)
        {
            _kinds[kind.Name] = kind;
        }

        foreach (var edgeType in KnowledgeEdgeTypes.Definitions)
        {
            _edgeTypes[edgeType.Name] = edgeType;
        }
    }

    public Task<KnowledgeNode> CreateNodeAsync(
        NodeDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(ToNode(CreateNodeCore(draft)));
        }
    }

    public Task<KnowledgeNode?> GetNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(
                _nodes.TryGetValue(id, out var node) ? ToNode(node, ResolvedIds()) : null);
        }
    }

    public Task<KnowledgeNode> UpdateNodeAsync(
        long id,
        NodePatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();

        if (patch.Name is null && patch.Description is null && patch.Content is null && patch.Properties is null)
        {
            throw new KnowledgeValidationException(
                "patch requires at least one of name, description, content or properties");
        }

        lock (_gate)
        {
            var node = RequireNode(id);
            if (patch.Name is not null)
            {
                node.Name = RequireName(patch.Name);
            }

            if (patch.Description is not null)
            {
                node.Description = patch.Description;
            }

            if (patch.Content is not null)
            {
                node.Content = patch.Content;
            }

            if (patch.Properties is not null)
            {
                node.Properties = new Dictionary<string, string>(patch.Properties, StringComparer.Ordinal);
            }

            node.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(ToNode(node));
        }
    }

    public Task<NodePage> QueryNodesAsync(
        NodeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(QueryCore(query));
        }
    }

    public Task<NodePage> SearchAsync(
        string text,
        NodeQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return Task.FromResult(new NodePage([], 0, 0, 0));
        }

        lock (_gate)
        {
            var effective = query ?? new NodeQuery();
            var candidates = FilterCandidates(effective, ResolvedIds()).ToList();
            var scored = new List<(NodeState Node, int Score)>();
            foreach (var node in candidates)
            {
                var score = terms.Count(term => Matches(node, term));
                if (score > 0)
                {
                    scored.Add((node, score));
                }
            }

            var ordered = scored
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Node.Id)
                .ToList();

            var limit = ClampLimit(effective.Limit);
            var offset = Math.Max(effective.Offset, 0);
            var items = ordered
                .Skip(offset)
                .Take(limit)
                .Select(pair => ToNode(pair.Node, effective.SummaryOnly))
                .ToArray();

            return Task.FromResult(new NodePage(items, ordered.Count, limit, offset));
        }
    }

    public Task<bool> DeleteNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_nodes.Remove(id))
            {
                return Task.FromResult(false);
            }

            foreach (var edge in _edges.Values
                         .Where(edge => edge.Source == id || edge.Destination == id)
                         .ToArray())
            {
                _edges.Remove(edge.Id);
            }

            return Task.FromResult(true);
        }
    }

    public Task<KnowledgeEdge> AddEdgeAsync(
        EdgeDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(ToEdge(AddEdgeCore(draft.Source, draft.Destination, draft.EdgeType)));
        }
    }

    public Task<bool> RemoveEdgeAsync(
        long source,
        long destination,
        string edgeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeType);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var type = Normalize(edgeType);
            var edge = _edges.Values.FirstOrDefault(candidate =>
                candidate.Source == source &&
                candidate.Destination == destination &&
                string.Equals(candidate.EdgeType, type, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(edge is not null && _edges.Remove(edge.Id));
        }
    }

    public Task<IReadOnlyList<NeighborResult>> GetNeighborsAsync(
        NeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireNode(query.NodeId);
            var resolved = ResolvedIds();
            var results = new List<NeighborResult>();

            foreach (var edge in _edges.Values.OrderBy(edge => edge.Id))
            {
                if (query.EdgeType is not null &&
                    !string.Equals(edge.EdgeType, query.EdgeType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (edge.Source == query.NodeId &&
                    query.Direction is EdgeDirection.Outgoing or EdgeDirection.Both &&
                    _nodes.TryGetValue(edge.Destination, out var destination))
                {
                    results.Add(new NeighborResult(ToNode(destination, resolved), ToEdge(edge), EdgeDirection.Outgoing));
                }

                if (edge.Destination == query.NodeId &&
                    query.Direction is EdgeDirection.Incoming or EdgeDirection.Both &&
                    _nodes.TryGetValue(edge.Source, out var source))
                {
                    results.Add(new NeighborResult(ToNode(source, resolved), ToEdge(edge), EdgeDirection.Incoming));
                }
            }

            return Task.FromResult<IReadOnlyList<NeighborResult>>(results);
        }
    }

    public Task<IReadOnlyList<NodeKindDefinition>> GetKindsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<NodeKindDefinition>>(
                _kinds.Values.OrderBy(kind => kind.Id).ToArray());
        }
    }

    public Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EdgeTypeDefinition>>(
                _edgeTypes.Values.OrderBy(type => type.Id).ToArray());
        }
    }

    public Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var connected = new HashSet<long>();
            foreach (var edge in _edges.Values)
            {
                connected.Add(edge.Source);
                connected.Add(edge.Destination);
            }

            var unanchored = _nodes.Values.Count(node =>
                node.Kind is KnowledgeKinds.Task or KnowledgeKinds.Goal && !IsAnchored(node));

            var duplicatePairs = _edges.Values
                .GroupBy(edge => (edge.Source, edge.Destination, edge.EdgeType))
                .Count(group => group.Count() > 1);

            return Task.FromResult(new GraphHealth(
                _nodes.Count,
                _edges.Count,
                _nodes.Keys.Count(id => !connected.Contains(id)),
                unanchored,
                duplicatePairs));
        }
    }

    public Task<GraphSnapshot> GetGraphAsync(
        GraphSnapshotQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var resolved = ResolvedIds();
            var nodes = _nodes.Values
                .OrderBy(node => node.Id)
                .Take(query.EffectiveMaxNodes)
                .Select(node => new GraphSnapshotNode(
                    node.Id,
                    node.Kind,
                    node.Name,
                    node.Description,
                    resolved.Contains(node.Id)))
                .ToArray();

            var included = nodes.Select(node => node.Id).ToHashSet();
            var edges = _edges.Values
                .Where(edge => included.Contains(edge.Source) && included.Contains(edge.Destination))
                .OrderBy(edge => edge.Id)
                .Take(query.EffectiveMaxEdges)
                .Select(edge => new GraphSnapshotEdge(edge.Id, edge.Source, edge.Destination, edge.EdgeType))
                .ToArray();

            return Task.FromResult(new GraphSnapshot(
                nodes,
                edges,
                _nodes.Count,
                _edges.Count,
                _nodes.Count > nodes.Length || _edges.Count > edges.Length));
        }
    }

    public Task<PlanResult> CreatePlanAsync(
        PlanDraft plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(CreatePlanCore(plan));
        }
    }

    public Task<KnowledgeNode> CreateTaskAsync(
        TaskDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (draft.Parent is not { } parentId)
            {
                throw new KnowledgeValidationException("a task requires a parent");
            }

            var parent = RequireNode(parentId);
            if (parent.Kind is not (KnowledgeKinds.Task or KnowledgeKinds.Goal))
            {
                throw new KnowledgeValidationException(
                    $"task parent must be a task or goal, got '{parent.Kind}'");
            }

            if (!IsAnchored(parent))
            {
                throw new KnowledgeValidationException(
                    $"part_of parent references node {parent.Id}, which is not attached to a repo via belongs_to");
            }

            var node = CreateNodeCore(new NodeDraft(
                KnowledgeKinds.Task,
                draft.Name,
                draft.Description,
                draft.Content));

            AddEdgeCore(node.Id, parentId, KnowledgeEdgeTypes.PartOf);

            foreach (var dependency in draft.DependsOn ?? [])
            {
                AddEdgeCore(node.Id, dependency, KnowledgeEdgeTypes.DependsOn);
            }

            foreach (var affect in draft.Affects ?? [])
            {
                AddEdgeCore(node.Id, affect, KnowledgeEdgeTypes.Affects);
            }

            return Task.FromResult(ToNode(node));
        }
    }

    public Task<KnowledgeNode> PatchTaskAsync(
        long id,
        TaskPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();

        if (patch.Name is null && patch.Description is null && patch.Content is null)
        {
            throw new KnowledgeValidationException("patch requires at least one of name, description or content");
        }

        lock (_gate)
        {
            var node = RequireNode(id);
            if (node.Kind != KnowledgeKinds.Task)
            {
                throw new KnowledgeValidationException($"node {id} is of kind '{node.Kind}', expected task");
            }

            if (patch.Name is not null)
            {
                node.Name = RequireName(patch.Name);
            }

            if (patch.Description is not null)
            {
                node.Description = patch.Description;
            }

            if (patch.Content is not null)
            {
                node.Content = patch.Content;
            }

            node.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(ToNode(node));
        }
    }

    private PlanResult CreatePlanCore(PlanDraft plan)
    {
        var tasks = plan.Tasks ?? [];
        ValidatePlanKeys(tasks);

        NodeState goal;
        if (plan.GoalId is { } goalId)
        {
            goal = RequireNode(goalId);
            if (goal.Kind != KnowledgeKinds.Goal)
            {
                throw new KnowledgeValidationException($"node {goalId} is of kind '{goal.Kind}', expected goal");
            }
        }
        else
        {
            goal = CreateNodeCore(new NodeDraft(
                KnowledgeKinds.Goal,
                plan.GoalName,
                plan.GoalDescription,
                plan.GoalContent));

            if (plan.Anchor is { } anchor)
            {
                AddEdgeCore(goal.Id, anchor.NodeId, anchor.EdgeType);
            }
        }

        if (plan.GoalId is not null && plan.Anchor is { } existingAnchor)
        {
            EnsureAnchor(goal, existingAnchor);
        }

        var created = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            created[task.Key] = CreateNodeCore(new NodeDraft(
                KnowledgeKinds.Task,
                task.Name,
                task.Description,
                task.Content,
                task.Properties));
        }

        foreach (var task in tasks)
        {
            var node = created[task.Key];
            var parentId = task.Parent is { } parentKey ? created[parentKey].Id : goal.Id;
            AddEdgeCore(node.Id, parentId, KnowledgeEdgeTypes.PartOf);

            foreach (var dependency in task.DependsOn ?? [])
            {
                AddEdgeCore(node.Id, created[dependency].Id, KnowledgeEdgeTypes.DependsOn);
            }

            foreach (var affect in task.Affects ?? [])
            {
                AddEdgeCore(node.Id, affect, KnowledgeEdgeTypes.Affects);
            }
        }

        var results = tasks
            .Select(task => new PlanTaskResult(
                task.Key,
                ToNode(created[task.Key]),
                task.DependsOn ?? []))
            .ToArray();

        return new PlanResult(ToNode(goal), results);
    }

    private static void ValidatePlanKeys(IReadOnlyList<PlanTaskDraft> tasks)
        => KnowledgePlanValidator.Validate(tasks);

    private NodePage QueryCore(NodeQuery query)
    {
        var resolved = ResolvedIds();
        var matching = FilterCandidates(query, resolved).ToList();
        var limit = ClampLimit(query.Limit);
        var offset = Math.Max(query.Offset, 0);

        var items = matching
            .OrderBy(node => node.Id)
            .Skip(offset)
            .Take(limit)
            .Select(node => ToNode(node, resolved, includeContent: !query.SummaryOnly))
            .ToArray();

        return new NodePage(items, matching.Count, limit, offset);
    }

    private IEnumerable<NodeState> FilterCandidates(NodeQuery query, HashSet<long> resolved)
    {
        IEnumerable<NodeState> candidates = _nodes.Values;

        if (query.Within is { } within)
        {
            RequireNode(within);
            var reachable = Reachable(within, query.Via, query.Descend);
            candidates = candidates.Where(node => reachable.Contains(node.Id));
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            var kind = Normalize(query.Kind);
            candidates = candidates.Where(node =>
                string.Equals(node.Kind, kind, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Properties is { Count: > 0 } properties)
        {
            candidates = candidates.Where(node => properties.All(pair =>
                node.Properties.TryGetValue(pair.Key, out var value) &&
                string.Equals(value, pair.Value, StringComparison.Ordinal)));
        }

        candidates = query.Scope switch
        {
            KnowledgeScope.Active => candidates.Where(node => !resolved.Contains(node.Id)),
            KnowledgeScope.Resolved => candidates.Where(node => resolved.Contains(node.Id)),
            _ => candidates,
        };

        return candidates;
    }

    /// <summary>
    /// One hop from the anchor along <paramref name="via"/> (both directions, or
    /// every edge when omitted), then optionally transitively downward (incoming
    /// edges) along <paramref name="descend"/>.
    /// </summary>
    private HashSet<long> Reachable(long start, string? via, string? descend)
    {
        var hop = new HashSet<long>();
        foreach (var edge in _edges.Values)
        {
            if (via is not null && !string.Equals(edge.EdgeType, via, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (edge.Source == start)
            {
                hop.Add(edge.Destination);
            }

            if (edge.Destination == start)
            {
                hop.Add(edge.Source);
            }
        }

        if (descend is null)
        {
            return hop;
        }

        var reachable = new HashSet<long>(hop);
        var queue = new Queue<long>(hop);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _edges.Values)
            {
                if (!string.Equals(edge.EdgeType, descend, StringComparison.OrdinalIgnoreCase) ||
                    edge.Destination != current)
                {
                    continue;
                }

                if (reachable.Add(edge.Source))
                {
                    queue.Enqueue(edge.Source);
                }
            }
        }

        return reachable;
    }

    private HashSet<long> ResolvedIds()
    {
        var resolvingTypes = _edgeTypes.Values
            .Where(type => type.Resolves)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (resolvingTypes.Count == 0)
        {
            return [];
        }

        return _edges.Values
            .Where(edge => resolvingTypes.Contains(edge.EdgeType))
            .Select(edge => edge.Destination)
            .ToHashSet();
    }

    private void EnsureAnchor(NodeState goal, PlanAnchor anchor)
    {
        var existing = _edges.Values
            .Where(edge => edge.Source == goal.Id &&
                           string.Equals(edge.EdgeType, anchor.EdgeType, StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.Destination)
            .ToArray();

        if (existing.Length == 0)
        {
            AddEdgeCore(goal.Id, anchor.NodeId, anchor.EdgeType);
            return;
        }

        if (existing.Contains(anchor.NodeId))
        {
            return;
        }

        throw new KnowledgeValidationException(
            $"goal {goal.Id} is already anchored via '{anchor.EdgeType}' to node {existing[0]}; " +
            $"it cannot be re-anchored to {anchor.NodeId}");
    }

    private bool IsAnchored(NodeState node)
    {
        var current = node;
        var seen = new HashSet<long>();
        while (seen.Add(current.Id))
        {
            if (current.Kind == KnowledgeKinds.Goal &&
                _edges.Values.Any(edge =>
                    edge.Source == current.Id &&
                    string.Equals(edge.EdgeType, KnowledgeEdgeTypes.BelongsTo, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var parentEdge = _edges.Values.FirstOrDefault(edge =>
                edge.Source == current.Id &&
                string.Equals(edge.EdgeType, KnowledgeEdgeTypes.PartOf, StringComparison.OrdinalIgnoreCase));

            if (parentEdge is null || !_nodes.TryGetValue(parentEdge.Destination, out current!))
            {
                return false;
            }
        }

        return false;
    }

    private NodeState CreateNodeCore(NodeDraft draft)
    {
        var kind = Normalize(draft.Kind);
        if (!_kinds.ContainsKey(kind))
        {
            if (!_options.AllowAdHocKinds)
            {
                throw new KnowledgeValidationException($"unknown node kind '{draft.Kind}'");
            }

            var nextId = _kinds.Count == 0 ? 1 : _kinds.Values.Max(existing => existing.Id) + 1;
            _kinds[kind] = new NodeKindDefinition(nextId, kind, "Ad-hoc kind.");
            _logger.LogInformation("Registered ad-hoc node kind {Kind}.", kind);
        }

        var now = DateTimeOffset.UtcNow;
        var node = new NodeState
        {
            Id = ++_nextNodeId,
            Kind = kind,
            Name = RequireName(draft.Name),
            Description = draft.Description ?? string.Empty,
            Content = draft.Content ?? string.Empty,
            Properties = draft.Properties is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(draft.Properties, StringComparer.Ordinal),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _nodes[node.Id] = node;
        return node;
    }

    private EdgeState AddEdgeCore(long sourceId, long destinationId, string edgeType)
    {
        var source = RequireNode(sourceId);
        var destination = RequireNode(destinationId);
        var typeName = Normalize(edgeType);

        if (!_edgeTypes.TryGetValue(typeName, out var definition))
        {
            throw new KnowledgeValidationException($"unknown edge type '{edgeType}'");
        }

        if (definition.SourceKinds.Count > 0 && !definition.SourceKinds.Contains(source.Kind))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' source references node {source.Id} of kind '{source.Kind}', expected one of {string.Join(", ", definition.SourceKinds)}");
        }

        if (definition.DestinationKinds.Count > 0 && !definition.DestinationKinds.Contains(destination.Kind))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' destination references node {destination.Id} of kind '{destination.Kind}', expected one of {string.Join(", ", definition.DestinationKinds)}");
        }

        var existing = _edges.Values.FirstOrDefault(edge =>
            edge.Source == sourceId &&
            edge.Destination == destinationId &&
            string.Equals(edge.EdgeType, definition.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        if (definition.SingleOutgoing &&
            _edges.Values.Any(edge =>
                edge.Source == sourceId &&
                string.Equals(edge.EdgeType, definition.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' allows one outgoing edge, and node {sourceId} already has one");
        }

        var created = new EdgeState
        {
            Id = ++_nextEdgeId,
            Source = sourceId,
            Destination = destinationId,
            EdgeType = definition.Name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _edges[created.Id] = created;
        return created;
    }

    private NodeState RequireNode(long id)
    {
        if (!_nodes.TryGetValue(id, out var node))
        {
            throw new KnowledgeNotFoundException($"node {id} does not exist");
        }

        return node;
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new KnowledgeValidationException("node name is required");
        }

        return name.Trim();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private int ClampLimit(int limit)
        => Math.Clamp(limit <= 0 ? 100 : limit, 1, _options.MaxPageSize);

    private static bool Matches(NodeState node, string term)
    {
        return node.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || node.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || node.Content.Contains(term, StringComparison.OrdinalIgnoreCase)
            || node.Properties.Any(pair =>
                pair.Key.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                pair.Value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static KnowledgeNode ToNode(NodeState node, bool includeContent = true) => new()
    {
        Id = node.Id,
        Kind = node.Kind,
        Name = node.Name,
        Description = node.Description,
        Content = includeContent ? node.Content : string.Empty,
        Properties = new Dictionary<string, string>(node.Properties, StringComparer.Ordinal),
        CreatedAt = node.CreatedAt,
        UpdatedAt = node.UpdatedAt,
    };

    private static KnowledgeNode ToNode(NodeState node, HashSet<long> resolved)
        => ToNode(node, resolved, includeContent: true);

    private static KnowledgeNode ToNode(NodeState node, HashSet<long> resolved, bool includeContent)
        => ToNode(node, includeContent) with { Resolved = resolved.Contains(node.Id) };

    private static KnowledgeEdge ToEdge(EdgeState edge) => new()
    {
        Id = edge.Id,
        Source = edge.Source,
        Destination = edge.Destination,
        EdgeType = edge.EdgeType,
        CreatedAt = edge.CreatedAt,
    };

    private sealed class NodeState
    {
        public required long Id { get; init; }

        public required string Kind { get; init; }

        public required string Name { get; set; }

        public required string Description { get; set; }

        public required string Content { get; set; }

        public required Dictionary<string, string> Properties { get; set; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class EdgeState
    {
        public required long Id { get; init; }

        public required long Source { get; init; }

        public required long Destination { get; init; }

        public required string EdgeType { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }
    }
}
