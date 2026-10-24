namespace Anima.Knowledge;

public sealed class KnowledgeOptions
{
    /// <summary>
    /// Register unknown node kinds on first write. This keeps agent-authored
    /// memories unblocked while the vocabulary evolves.
    /// </summary>
    public bool AllowAdHocKinds { get; set; } = true;

    /// <summary>Maximum page size accepted from a single query.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>Publish compact graph events on the bus after successful mutations.</summary>
    public bool PublishEvents { get; set; } = true;
}
