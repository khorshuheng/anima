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
/// One bounded view of the graph: nodes ordered by id, then edges ordered by id and
/// kept only when both endpoints are among the included nodes. <see cref="NodeCount"/>
/// and <see cref="EdgeCount"/> are graph totals, so <see cref="Truncated"/> tells the
/// caller the payload is a prefix: <c>maxNodes</c>/<c>maxEdges</c> cut it, or the node
/// cap excluded nodes that some edges reference.
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
