using System.Text.Json.Serialization;

namespace Anima.Knowledge;

/// <summary>
/// One authored vocabulary pack. The core pack ships embedded in this assembly;
/// user packs have the same shape and are merged additively on top of it.
/// </summary>
public sealed record VocabularyPack
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the pack document; a mismatch fails startup.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Pack identifier, used in validation messages. Required for user packs.</summary>
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<NodeKindSpec> NodeKinds { get; init; } = [];

    public IReadOnlyList<EdgeTypeSpec> EdgeTypes { get; init; } = [];
}

/// <summary>
/// One loaded vocabulary: the merged catalog plus the packs that produced it, so a
/// candidate pack can be validated against the same baseline the service is running.
/// </summary>
public sealed record KnowledgeVocabularyLoad(
    KnowledgeVocabularyCatalog Catalog,
    IReadOnlyList<VocabularyPack> Packs);

/// <summary>A node kind as authored, before merge and id assignment.</summary>
public sealed record NodeKindSpec
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Explicit id. Core entries stay below the pack floor; packs use 1000+.</summary>
    public int? Id { get; init; }

    /// <summary>Property keys every node of this kind must carry.</summary>
    public IReadOnlyList<string> RequiredProps { get; init; } = [];

    /// <summary>
    /// Vocabulary traits consumed by code that would otherwise compare kind names,
    /// for example <c>plannable</c> or <c>scope_root</c>.
    /// </summary>
    public IReadOnlyList<string> Traits { get; init; } = [];

    /// <summary>Deprecated kinds stay readable and are hidden from suggestion reads.</summary>
    public bool Deprecated { get; init; }
}

/// <summary>An edge type as authored, before merge and id assignment.</summary>
public sealed record EdgeTypeSpec
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Explicit id. Core entries stay below the pack floor; packs use 1000+.</summary>
    public int? Id { get; init; }

    /// <summary>Allowed source kinds. An empty list means any kind.</summary>
    public IReadOnlyList<string> SourceKinds { get; init; } = [];

    /// <summary>Allowed destination kinds. An empty list means any kind.</summary>
    public IReadOnlyList<string> DestinationKinds { get; init; } = [];

    /// <summary>
    /// Explicit endpoint pairs. Canonical form: when present, the pair list is used
    /// verbatim and <see cref="SourceKinds"/>/<see cref="DestinationKinds"/> must be empty.
    /// </summary>
    public IReadOnlyList<EdgeKindPair> Pairs { get; init; } = [];

    /// <summary>Edge type that expresses this relation in the opposite direction.</summary>
    public string? InverseName { get; init; }

    /// <summary>
    /// The inverse of a symmetric edge, so its pairs must be closed under swap.
    /// A null flag on a widening entry means "inherit from the defining pack".
    /// </summary>
    public bool? Symmetric { get; init; }

    /// <summary>The relation may not form a cycle.</summary>
    public bool? Acyclic { get; init; }

    /// <summary>An incoming edge of this type resolves its destination node.</summary>
    public bool? Resolves { get; init; }

    /// <summary>A source node may carry at most one outgoing edge of this type.</summary>
    public bool? SingleOutgoing { get; init; }

    /// <summary>
    /// Vocabulary traits, for example <c>hierarchy</c> or <c>anchor</c>. On a pack
    /// entry that widens an existing edge type, omit traits to inherit them.
    /// </summary>
    public IReadOnlyList<string> Traits { get; init; } = [];

    /// <summary>Deprecated edge types stay readable and are hidden from suggestion reads.</summary>
    public bool? Deprecated { get; init; }
}

/// <summary>
/// An allowed (source kind, destination kind) endpoint pair, authored in JSON as a
/// two-element array such as <c>["invoice", "repo"]</c>. <c>*</c> is a wildcard.
/// </summary>
[JsonConverter(typeof(EdgeKindPairJsonConverter))]
public readonly record struct EdgeKindPair(string Source, string Destination);
