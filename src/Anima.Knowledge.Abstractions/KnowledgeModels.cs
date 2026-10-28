namespace Anima.Knowledge;

/// <summary>A node in the knowledge graph. Tasks and memories share this shape.</summary>
public sealed record KnowledgeNode
{
    public required long Id { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Derived: true when an incoming edge from a resolving edge type exists.</summary>
    public bool Resolved { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record KnowledgeEdge
{
    public required long Id { get; init; }

    public required long Source { get; init; }

    public required long Destination { get; init; }

    public required string EdgeType { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record NodeKindDefinition(int Id, string Name, string Description)
{
    /// <summary>Property keys every node of this kind must carry.</summary>
    public IReadOnlyList<string> RequiredProps { get; init; } = [];

    /// <summary>Traits consumed by code that would otherwise compare kind names.</summary>
    public IReadOnlyList<string> Traits { get; init; } = [];

    /// <summary>Pack that defined this kind: <c>core</c> or a pack name.</summary>
    public string Source { get; init; } = KnowledgeVocabularyCatalog.CorePack;

    public bool Deprecated { get; init; }
}
public sealed record EdgeTypeDefinition(
    int Id,
    string Name,
    string Description,
    IReadOnlySet<string> SourceKinds,
    IReadOnlySet<string> DestinationKinds,
    bool Resolves = false,
    bool SingleOutgoing = false)
{
    /// <summary>Normalized endpoint pairs; an empty kind set with a wildcard pair means any kind.</summary>
    public IReadOnlyList<EdgeKindPair> Pairs { get; init; } = [];

    /// <summary>Edge type expressing this relation in the opposite direction.</summary>
    public string? InverseName { get; init; }

    public bool Symmetric { get; init; }

    /// <summary>The relation may not form a cycle.</summary>
    public bool Acyclic { get; init; }

    /// <summary>Traits consumed by code that would otherwise compare edge type names.</summary>
    public IReadOnlyList<string> Traits { get; init; } = [];

    /// <summary>Pack that defined this edge type: <c>core</c> or a pack name.</summary>
    public string Source { get; init; } = KnowledgeVocabularyCatalog.CorePack;

    public bool Deprecated { get; init; }
}

public sealed record NodePatch(
    string? Name = null,
    string? Description = null,
    string? Content = null,
    IReadOnlyDictionary<string, string>? Properties = null);

public sealed record NodeDraft(
    string Kind,
    string Name,
    string? Description = null,
    string? Content = null,
    IReadOnlyDictionary<string, string>? Properties = null);

public sealed record EdgeDraft(long Source, long Destination, string EdgeType);

public sealed record TaskDraft(
    string Name,
    string? Description = null,
    string? Content = null,
    long? Parent = null,
    IReadOnlyList<long>? DependsOn = null,
    IReadOnlyList<long>? Affects = null);

public sealed record TaskPatch(string? Name = null, string? Description = null, string? Content = null);

public sealed record PlanTaskDraft(
    string Key,
    string Name,
    string? Description = null,
    string? Content = null,
    IReadOnlyDictionary<string, string>? Properties = null,
    string? Parent = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<long>? Affects = null);

public sealed record PlanAnchor(long NodeId, string EdgeType);

public sealed record PlanDraft(
    string GoalName,
    string? GoalDescription = null,
    string? GoalContent = null,
    PlanAnchor? Anchor = null,
    long? GoalId = null,
    IReadOnlyList<PlanTaskDraft>? Tasks = null);

public sealed record PlanTaskResult(string Key, KnowledgeNode Node, IReadOnlyList<string> DependsOn);

public sealed record PlanResult(KnowledgeNode? Goal, IReadOnlyList<PlanTaskResult> Tasks);
