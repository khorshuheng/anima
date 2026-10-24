namespace Anima.Knowledge;

public enum KnowledgeScope
{
    Active,
    All,
    Resolved,
}

public enum EdgeDirection
{
    Outgoing,
    Incoming,
    Both,
}

public sealed record NodeQuery
{
    public string? Kind { get; init; }

    public KnowledgeScope Scope { get; init; } = KnowledgeScope.Active;

    public long? Within { get; init; }

    public string? Via { get; init; }

    public string? Descend { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }

    /// <summary>Exact-match property filters; all must match.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>Drop content from the result to save agent context.</summary>
    public bool SummaryOnly { get; init; }
}

public sealed record NodePage(IReadOnlyList<KnowledgeNode> Items, int Total, int Limit, int Offset);

public sealed record NeighborQuery(
    long NodeId,
    EdgeDirection Direction = EdgeDirection.Both,
    string? EdgeType = null);

public sealed record NeighborResult(KnowledgeNode Node, KnowledgeEdge Edge, EdgeDirection Direction);

public sealed record GraphHealth(
    int NodeCount,
    int EdgeCount,
    int IsolatedNodes,
    int UnanchoredWork,
    int DuplicateEdgePairs);
