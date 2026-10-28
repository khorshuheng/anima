namespace Anima.Knowledge;

public sealed class KnowledgeOptions
{
    /// <summary>
    /// Register unknown node kinds on first write. This keeps agent-authored
    /// memories unblocked while the vocabulary evolves.
    /// </summary>
    public bool AllowAdHocKinds { get; set; } = true;

    /// <summary>
    /// Extra vocabulary pack paths, directories or <c>*.json</c> globs. They are
    /// applied after the embedded core pack, the repo <c>config/knowledge</c>
    /// directory and the user <c>~/.config/anima/knowledge</c> directory.
    /// </summary>
    public IReadOnlyList<string> Packs { get; set; } = [];

    /// <summary>
    /// How an invalid vocabulary pack is handled at startup. <c>Strict</c> (default)
    /// fails startup; <c>Warn</c> logs the offending pack and skips it.
    /// </summary>
    public VocabularyMode VocabularyMode { get; set; } = VocabularyMode.Strict;

    /// <summary>Maximum page size accepted from a single query.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>Publish compact graph events on the bus after successful mutations.</summary>
    public bool PublishEvents { get; set; } = true;
}
