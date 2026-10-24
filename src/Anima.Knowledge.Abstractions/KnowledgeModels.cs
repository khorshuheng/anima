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

public sealed record NodeKindDefinition(int Id, string Name, string Description);

public sealed record EdgeTypeDefinition(
    int Id,
    string Name,
    string Description,
    IReadOnlySet<string> SourceKinds,
    IReadOnlySet<string> DestinationKinds,
    bool Resolves = false,
    bool SingleOutgoing = false);

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
