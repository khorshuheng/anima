namespace Anima.Knowledge.Core.Tests;

/// <summary>
/// Pack discovery order and shape. The order is application order, because a later
/// pack may widen what an earlier one added.
/// </summary>
public sealed class PackDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "anima-packs-" + Guid.NewGuid().ToString("n"));

    public PackDiscoveryTests()
    {
        Directory.CreateDirectory(RepoPacks);
        Directory.CreateDirectory(UserPacks);
    }

    private string RepoRoot => Path.Combine(_root, "repo");

    private string UserRoot => Path.Combine(_root, "user");

    private string RepoPacks => Path.Combine(RepoRoot, "config", "knowledge");

    private string UserPacks => Path.Combine(UserRoot, "anima", "knowledge");

    [Fact]
    public void Discovers_repo_then_user_then_configured_packs()
    {
        string repoB = Touch(RepoPacks, "b.json");
        string repoA = Touch(RepoPacks, "a.json");
        string user = Touch(UserPacks, "c-extra.json");
        string explicitFile = Touch(Path.Combine(_root, "explicit"), "d.json");

        var discovered = KnowledgeVocabularyLoader.DiscoverPackFiles(
            [explicitFile, Path.Combine(_root, "explicit", "*.json"), Path.Combine(_root, "explicit")],
            RepoRoot,
            UserRoot);

        Assert.Equal(new[] { repoA, repoB, user, explicitFile }, discovered.ToArray());
    }

    [Fact]
    public void Missing_default_directories_are_ignored()
    {
        var discovered = KnowledgeVocabularyLoader.DiscoverPackFiles(
            configured: null,
            repoRoot: Path.Combine(_root, "does-not-exist"),
            userConfigRoot: Path.Combine(_root, "also-missing"));

        Assert.Empty(discovered);
    }

    [Fact]
    public void Missing_explicit_pack_is_reported_by_the_loader()
    {
        string missing = Path.Combine(_root, "nope", "missing.json");
        var discovered = KnowledgeVocabularyLoader.DiscoverPackFiles([missing], RepoRoot, UserRoot);

        Assert.Equal(new[] { Path.GetFullPath(missing) }, discovered.ToArray());
        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Load(discovered));
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovered_packs_are_applied_in_order()
    {
        File.WriteAllText(Path.Combine(RepoPacks, "10-base.json"), """
            { "schema_version": 1, "name": "base",
              "node_kinds": [{ "name": "base:invoice" }] }
            """);
        File.WriteAllText(Path.Combine(UserPacks, "20-widen.json"), """
            { "schema_version": 1, "name": "widen",
              "edge_types": [{ "name": "related_to", "pairs": [["base:invoice", "repo"]] }] }
            """);

        var catalog = KnowledgeVocabularyLoader.Load(
            KnowledgeVocabularyLoader.DiscoverPackFiles(configured: null, repoRoot: RepoRoot, userConfigRoot: UserRoot));

        Assert.True(catalog.AllowsPair(KnowledgeEdgeTypes.RelatedTo, "base:invoice", KnowledgeKinds.Repo));
        Assert.Equal("base", catalog.RequireKind("base:invoice").Source);
        // Widening keeps the defining pack as the owner of the entry.
        Assert.Equal(KnowledgeVocabularyCatalog.CorePack, catalog.RequireEdgeType(KnowledgeEdgeTypes.RelatedTo).Source);
    }

    [Fact]
    public void Loaded_packs_let_a_candidate_widen_another_packs_entry()
    {
        Directory.CreateDirectory(Path.Combine(RepoPacks, "..", ".."));
        File.WriteAllText(Path.Combine(RepoPacks, "10-base.json"), """
            { "schema_version": 1, "name": "base",
              "node_kinds": [{ "name": "base:invoice" }] }
            """);
        File.WriteAllText(Path.Combine(UserPacks, "20-widen.json"), """
            { "schema_version": 1, "name": "widen",
              "edge_types": [{ "name": "related_to", "pairs": [["base:invoice", "repo"]] }] }
            """);
        var paths = KnowledgeVocabularyLoader.DiscoverPackFiles([], RepoRoot, UserRoot);

        var loaded = KnowledgeVocabularyLoader.LoadWithPacks(paths);
        Assert.Equal(2, loaded.Packs.Count);

        // Candidate widening the same core edge, referencing another pack's kind.
        var candidate = KnowledgeVocabularyLoader.Parse(
            """
            { "schema_version": 1, "name": "candidate",
              "edge_types": [{ "name": "related_to", "pairs": [["base:invoice", "note"]] }] }
            """,
            "candidate");

        var merged = KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [.. loaded.Packs, candidate]);
        Assert.True(merged.AllowsPair(KnowledgeEdgeTypes.RelatedTo, "base:invoice", KnowledgeKinds.Note));

        // Without the loaded packs the same candidate cannot resolve the kind, which is
        // why the validation endpoint validates on top of them.
        Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [candidate]));
    }

    private static string Touch(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, """{ "schema_version": 1, "name": "pack" }""");
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
