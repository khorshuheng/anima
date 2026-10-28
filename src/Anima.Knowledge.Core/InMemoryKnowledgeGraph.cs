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
    private readonly Lock _gate = new();
    private readonly KnowledgeOptions _options;
    private readonly ILogger<InMemoryKnowledgeGraph> _logger;
    private readonly Dictionary<long, NodeState> _nodes = [];
    private readonly Dictionary<long, EdgeState> _edges = [];
    private readonly Dictionary<string, NodeKindDefinition> _kinds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EdgeTypeDefinition> _edgeTypes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly KnowledgeVocabularyCatalog _vocabulary;
    private readonly HashSet<string> _plannableKinds;
    private readonly HashSet<string> _hierarchyEdges;
    private readonly HashSet<string> _anchorEdges;

    private long _nextNodeId;
    private long _nextEdgeId;

    public InMemoryKnowledgeGraph(
        IOptions<KnowledgeOptions> options,
        ILogger<InMemoryKnowledgeGraph> logger,
        KnowledgeVocabularyCatalog vocabulary)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(vocabulary);
        _options = options.Value;
        _logger = logger;
        _vocabulary = vocabulary;

        foreach (var kind in vocabulary.NodeKinds)
        {
            _kinds[kind.Name] = kind;
        }

        foreach (var edgeType in vocabulary.EdgeTypes)
        {
            _edgeTypes[edgeType.Name] = edgeType;
        }

        _plannableKinds = Names(vocabulary.NodeKindsWithTrait(KnowledgeTraits.Plannable).Select(kind => kind.Name));
        _hierarchyEdges = Names(vocabulary.EdgeTypesWithTrait(KnowledgeTraits.Hierarchy).Select(edge => edge.Name));
        _anchorEdges = Names(vocabulary.EdgeTypesWithTrait(KnowledgeTraits.Anchor).Select(edge => edge.Name));
    }

    private static HashSet<string> Names(IEnumerable<string> names)
        => names.ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            // A patch replaces the property dictionary, so the requirement applies to the
            // replacement set, not to a merge with the current one.
            if (patch.Properties is not null && _kinds.TryGetValue(node.Kind, out var nodeKind))
            {
                KnowledgePropertyRules.EnsureRequired(nodeKind, patch.Properties);
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
        string[] terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                int score = terms.Count(term => Matches(node, term));
                if (score > 0)
                {
                    scored.Add((node, score));
                }
            }

            var ordered = scored
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Node.Id)
                .ToList();

            int limit = ClampLimit(effective.Limit);
            int offset = Math.Max(effective.Offset, 0);
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
            string type = Normalize(edgeType);
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

            string? edgeType = null;
            var direction = query.Direction;
            if (query.EdgeType is not null)
            {
                if (!_vocabulary.TryResolveEdgeType(query.EdgeType, out var resolution))
                {
                    return Task.FromResult<IReadOnlyList<NeighborResult>>(results);
                }

                edgeType = resolution.Definition.Name;
                direction = EffectiveDirection(resolution, direction);
            }

            foreach (var edge in _edges.Values.OrderBy(edge => edge.Id))
            {
                if (edgeType is not null &&
                    !string.Equals(edge.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (edge.Source == query.NodeId &&
                    direction is EdgeDirection.Outgoing or EdgeDirection.Both &&
                    _nodes.TryGetValue(edge.Destination, out var destination))
                {
                    results.Add(new NeighborResult(ToNode(destination, resolved), ToEdge(edge), EdgeDirection.Outgoing));
                }

                if (edge.Destination == query.NodeId &&
                    direction is EdgeDirection.Incoming or EdgeDirection.Both &&
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
                [.. _kinds.Values.Where(kind => !kind.Deprecated).OrderBy(kind => kind.Id)]);
        }
    }

    public Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EdgeTypeDefinition>>(
                [.. _edgeTypes.Values.Where(type => !type.Deprecated).OrderBy(type => type.Id)]);
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

            int unanchored = _nodes.Values.Count(node =>
                _plannableKinds.Contains(node.Kind) && !IsAnchored(node));

            int duplicatePairs = _edges.Values
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
            var candidates = query.SinceId is { } sinceId
                ? _nodes.Values.Where(node => node.Id > sinceId)
                : _nodes.Values;
            var window = query.Newest
                ? candidates.OrderByDescending(node => node.Id)
                : candidates.OrderBy(node => node.Id);

            var nodes = window
                .Take(query.EffectiveMaxNodes)
                .Select(node => new GraphSnapshotNode(
                    node.Id,
                    node.Kind,
                    node.Name,
                    node.Description,
                    resolved.Contains(node.Id)))
                .ToArray();

            var included = nodes.Select(node => node.Id).ToHashSet();
            var internalEdges = _edges.Values
                .Where(edge => included.Contains(edge.Source) && included.Contains(edge.Destination));
            var edgeWindow = query.Newest
                ? internalEdges.OrderByDescending(edge => edge.Id)
                : internalEdges.OrderBy(edge => edge.Id);

            var edges = edgeWindow
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
            if (!_plannableKinds.Contains(parent.Kind))
            {
                throw new KnowledgeValidationException(
                    $"task parent must be a plannable kind, got '{parent.Kind}'");
            }

            if (!IsAnchored(parent))
            {
                throw new KnowledgeValidationException(
                    $"parent references node {parent.Id}, which is not anchored");
            }

            var node = CreateNodeCore(new NodeDraft(
                KnowledgeKinds.Task,
                draft.Name,
                draft.Description,
                draft.Content));

            AddEdgeCore(node.Id, parentId, KnowledgeEdgeTypes.PartOf);

            foreach (long dependency in draft.DependsOn ?? [])
            {
                AddEdgeCore(node.Id, dependency, KnowledgeEdgeTypes.DependsOn);
            }

            foreach (long affect in draft.Affects ?? [])
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
            long parentId = task.Parent is { } parentKey ? created[parentKey].Id : goal.Id;
            AddEdgeCore(node.Id, parentId, KnowledgeEdgeTypes.PartOf);

            foreach (string dependency in task.DependsOn ?? [])
            {
                AddEdgeCore(node.Id, created[dependency].Id, KnowledgeEdgeTypes.DependsOn);
            }

            foreach (long affect in task.Affects ?? [])
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
        int limit = ClampLimit(query.Limit);
        int offset = Math.Max(query.Offset, 0);

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
            string kind = Normalize(query.Kind);
            candidates = candidates.Where(node =>
                string.Equals(node.Kind, kind, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Properties is { Count: > 0 } properties)
        {
            candidates = candidates.Where(node => properties.All(pair =>
                node.Properties.TryGetValue(pair.Key, out string? value) &&
                string.Equals(value, pair.Value, StringComparison.Ordinal)));
        }

        return query.Scope switch
        {
            KnowledgeScope.Active => candidates.Where(node => !resolved.Contains(node.Id)),
            KnowledgeScope.Resolved => candidates.Where(node => resolved.Contains(node.Id)),
            _ => candidates,
        };
    }

    /// <summary>
    /// One hop from the anchor along <paramref name="via"/> (both directions, or
    /// every edge when omitted), then optionally transitively downward (incoming
    /// edges) along <paramref name="descend"/>.
    /// </summary>
    private HashSet<long> Reachable(long start, string? via, string? descend)
    {
        string? viaType = CanonicalEdgeType(via);
        var hop = new HashSet<long>();
        foreach (var edge in _edges.Values)
        {
            if (viaType is not null && !string.Equals(edge.EdgeType, viaType, StringComparison.OrdinalIgnoreCase))
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

        if (descend is null || !_vocabulary.TryResolveEdgeType(descend, out var descendResolution))
        {
            return hop;
        }

        // descend narrows away from the hop nodes. Children are always the edge's
        // source side (they point at their parent), so naming the relation by its
        // inverse changes which edges match, never the walk direction.
        string descendType = descendResolution.Definition.Name;
        var reachable = new HashSet<long>(hop);
        var queue = new Queue<long>(hop);
        while (queue.Count > 0)
        {
            long current = queue.Dequeue();
            foreach (var edge in _edges.Values)
            {
                if (!string.Equals(edge.EdgeType, descendType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (edge.Destination != current || !reachable.Add(edge.Source))
                {
                    continue;
                }

                queue.Enqueue(edge.Source);
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

        return [.. _edges.Values
            .Where(edge => resolvingTypes.Contains(edge.EdgeType))
            .Select(edge => edge.Destination)];
    }

    private void EnsureAnchor(NodeState goal, PlanAnchor anchor)
    {
        long[] existing = [.. _edges.Values
            .Where(edge => edge.Source == goal.Id &&
                           string.Equals(edge.EdgeType, anchor.EdgeType, StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.Destination)];

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
            if (_edges.Values.Any(edge => edge.Source == current.Id && _anchorEdges.Contains(edge.EdgeType)))
            {
                return true;
            }

            var parentEdge = _edges.Values.FirstOrDefault(edge =>
                edge.Source == current.Id && _hierarchyEdges.Contains(edge.EdgeType));

            if (parentEdge is null || !_nodes.TryGetValue(parentEdge.Destination, out current!))
            {
                return false;
            }
        }

        return false;
    }

    private NodeState CreateNodeCore(NodeDraft draft)
    {
        string kind = Normalize(draft.Kind);
        if (!_kinds.TryGetValue(kind, out var kindDefinition))
        {
            if (!_options.AllowAdHocKinds)
            {
                throw new KnowledgeValidationException($"unknown node kind '{draft.Kind}'");
            }

            int nextId = _kinds.Count == 0 ? 1 : _kinds.Values.Max(existing => existing.Id) + 1;
            kindDefinition = new NodeKindDefinition(nextId, kind, "Ad-hoc kind.");
            _kinds[kind] = kindDefinition;
            _logger.LogInformation("Registered ad-hoc node kind {Kind}.", kind);
        }

        if (kindDefinition.Deprecated)
        {
            _logger.LogWarning("Writing node kind {Kind}, which the vocabulary marks deprecated.", kind);
        }

        KnowledgePropertyRules.EnsureRequired(kindDefinition, draft.Properties);

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
        string typeName = Normalize(edgeType);

        if (!_edgeTypes.TryGetValue(typeName, out var definition))
        {
            throw InverseEdgeTypeException(edgeType);
        }

        if (definition.Deprecated)
        {
            _logger.LogWarning("Writing edge type {EdgeType}, which the vocabulary marks deprecated.", definition.Name);
        }

        if (!_vocabulary.AllowsPair(definition.Name, source.Kind, destination.Kind))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' does not allow {source.Kind} -> {destination.Kind}; allowed pairs: {DescribePairs(definition)}");
        }

        var existing = _edges.Values.FirstOrDefault(edge =>
            edge.Source == sourceId &&
            edge.Destination == destinationId &&
            string.Equals(edge.EdgeType, definition.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        // A symmetric relation is undirected: writing the mirror would store the same
        // fact twice, and reads would then report the same neighbour twice.
        if (definition.Symmetric)
        {
            var mirrored = _edges.Values.FirstOrDefault(edge =>
                edge.Source == destinationId &&
                edge.Destination == sourceId &&
                string.Equals(edge.EdgeType, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (mirrored is not null)
            {
                return mirrored;
            }
        }

        if (definition.Acyclic && Reaches(definition.Name, from: destinationId, to: sourceId))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' is acyclic, and adding {sourceId} -> {destinationId} would close a cycle");
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

    /// <summary>
    /// Lower case is the stored vocabulary name format; see
    /// <see cref="KnowledgeVocabularyLoader.NormalizeName"/> for the wire contract.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Lower case is the stored vocabulary name format; changing it would break existing rows.")]
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string DescribePairs(EdgeTypeDefinition definition)
        => definition.Pairs.Count == 0
            ? "any"
            : string.Join(", ", definition.Pairs.Select(pair => $"{pair.Source}->{pair.Destination}"));

    /// <summary>True when <paramref name="to"/> is reachable from <paramref name="from"/> along the edge type.</summary>
    private bool Reaches(string edgeType, long from, long to)
    {
        var seen = new HashSet<long> { from };
        var queue = new Queue<long>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            long current = queue.Dequeue();
            foreach (var edge in _edges.Values)
            {
                if (edge.Source != current ||
                    !string.Equals(edge.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (edge.Destination == to)
                {
                    return true;
                }

                if (seen.Add(edge.Destination))
                {
                    queue.Enqueue(edge.Destination);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A symmetric relation is undirected however it is stored, and an inverse name
    /// reads the stored edge from the opposite end.
    /// </summary>
    private static EdgeDirection EffectiveDirection(EdgeTypeResolution resolution, EdgeDirection requested)
    {
        if (resolution.Definition.Symmetric)
        {
            return EdgeDirection.Both;
        }

        return resolution.Reversed ? Flip(requested) : requested;
    }

    private static EdgeDirection Flip(EdgeDirection direction) => direction switch
    {
        EdgeDirection.Outgoing => EdgeDirection.Incoming,
        EdgeDirection.Incoming => EdgeDirection.Outgoing,
        _ => EdgeDirection.Both,
    };

    /// <summary>Canonical stored name for a traversal filter, or null when unset.</summary>
    private string? CanonicalEdgeType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _vocabulary.TryGetCanonicalEdgeTypeName(name, out string? canonical)
            ? canonical
            : Normalize(name);
    }

    /// <summary>
    /// Edges are stored in the canonical direction only. Naming the inverse on a write
    /// is a mistake worth reporting precisely, not a silent swap.
    /// </summary>
    private KnowledgeValidationException InverseEdgeTypeException(string edgeType)
        => _vocabulary.TryResolveEdgeType(edgeType, out var resolution) && resolution.Reversed
            ? new KnowledgeValidationException(
                $"'{edgeType}' is the inverse of '{resolution.Definition.Name}'; add the edge as '{resolution.Definition.Name}' from the opposite endpoint")
            : new KnowledgeValidationException($"unknown edge type '{edgeType}'");

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
