namespace Anima.Knowledge;

/// <summary>
/// How the vocabulary loader treats an invalid pack at startup.
/// </summary>
public enum VocabularyMode
{
    /// <summary>An invalid or conflicting pack fails startup with the reason.</summary>
    Strict,

    /// <summary>The offending pack is reported and skipped; the rest still loads.</summary>
    Warn,
}
