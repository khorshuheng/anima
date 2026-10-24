using System.Text.Json.Serialization;

namespace Anima.Knowledge;

/// <summary>
/// Bounded whole-graph read for the dashboard. Caps are clamped to
/// <see cref="MinCap"/>..<see cref="MaxCap"/> so a single response stays bounded.
/// </summary>
public sealed record GraphSnapshotQuery
{
    public const int MinCap = 1;

    public const int MaxCap = 5000;

    public const int DefaultMaxNodes = 500;

    public const int DefaultMaxEdges = 1000;

    public int MaxNodes { get; init; } = DefaultMaxNodes;

    public int MaxEdges { get; init; } = DefaultMaxEdges;

    /// <summary>Only include nodes with an id greater than this, for incremental reads.</summary>
    public long? SinceId { get; init; }

    /// <summary>
    /// Walk the window newest first. The dashboard uses this so the active workflow's
    /// recently created nodes stay inside the cap instead of the graph's first nodes.
    /// </summary>
    public bool Newest { get; init; }

    public int EffectiveMaxNodes => Math.Clamp(MaxNodes, MinCap, MaxCap);

    public int EffectiveMaxEdges => Math.Clamp(MaxEdges, MinCap, MaxCap);
}

/// <summary>A node summary in a <see cref="GraphSnapshot"/>; content is never transferred.</summary>
public sealed record GraphSnapshotNode(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("resolved")] bool Resolved);

public sealed record GraphSnapshotEdge(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("source")] long Source,
    [property: JsonPropertyName("destination")] long Destination,
    [property: JsonPropertyName("edgeType")] string EdgeType);

/// <summary>
/// One bounded view of the graph: a window of at most <c>maxNodes</c> nodes in
/// ascending id order (descending with <see cref="GraphSnapshotQuery.Newest"/>),
/// then at most <c>maxEdges</c> edges in the same direction, kept only when both
/// endpoints are inside the node window. <see cref="NodeCount"/> and
/// <see cref="EdgeCount"/> are graph totals, so <see cref="Truncated"/> tells the
/// caller the payload is a window: the caps cut it, or the node cap excluded nodes
/// that some edges reference.
/// </summary>
/// <remarks>
/// The JSON names are pinned with attributes because this contract is camelCase on the
/// wire while the knowledge service serializes everything else snake_case.
/// </remarks>
public sealed record GraphSnapshot(
    [property: JsonPropertyName("nodes")] IReadOnlyList<GraphSnapshotNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<GraphSnapshotEdge> Edges,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("truncated")] bool Truncated);
