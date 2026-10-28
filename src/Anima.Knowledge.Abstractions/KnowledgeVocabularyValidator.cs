namespace Anima.Knowledge;

/// <summary>
/// Outcome of validating a candidate pack against the vocabulary a service is running.
/// </summary>
public sealed record VocabularyValidationResult(
    bool Valid,
    string PackName,
    int NodeKinds,
    int EdgeTypes,
    int AddedNodeKinds,
    int AddedEdgeTypes,
    string? Message)
{
    public static VocabularyValidationResult Invalid(string message)
        => new(false, string.Empty, 0, 0, 0, 0, message);
}

/// <summary>
/// Validates a candidate pack document on top of the packs already loaded. Extraction
/// keeps the HTTP handler free of merge logic and makes the check testable without a host.
/// </summary>
public static class KnowledgeVocabularyValidator
{
    public static VocabularyValidationResult Validate(
        string json,
        IEnumerable<VocabularyPack> loadedPacks)
    {
        ArgumentNullException.ThrowIfNull(loadedPacks);

        try
        {
            var pack = KnowledgeVocabularyLoader.Parse(json, "candidate");
            var catalog = KnowledgeVocabularyLoader.Build(
                KnowledgeVocabularyLoader.LoadEmbeddedCore(),
                [.. loadedPacks, pack]);

            return new VocabularyValidationResult(
                Valid: true,
                PackName: pack.Name,
                NodeKinds: catalog.NodeKinds.Count,
                EdgeTypes: catalog.EdgeTypes.Count,
                AddedNodeKinds: CountOwned(catalog.NodeKinds, pack.Name),
                AddedEdgeTypes: CountOwned(catalog.EdgeTypes, pack.Name),
                Message: null);
        }
        catch (KnowledgeVocabularyException exception)
        {
            return VocabularyValidationResult.Invalid(exception.Message);
        }
    }

    private static int CountOwned(IReadOnlyList<NodeKindDefinition> kinds, string packName)
        => kinds.Count(kind => string.Equals(kind.Source, packName, StringComparison.OrdinalIgnoreCase));

    private static int CountOwned(IReadOnlyList<EdgeTypeDefinition> edgeTypes, string packName)
        => edgeTypes.Count(type => string.Equals(type.Source, packName, StringComparison.OrdinalIgnoreCase));
}
