namespace Anima.Knowledge.Core.Tests;

/// <summary>
/// Distribution guards for vocabulary packs. Repo packs are discovered under
/// <c>$ANIMA_REPO_ROOT/config/knowledge</c>, so they only ship if the image actually
/// carries that directory; the failure mode is silent (core-only vocabulary in the
/// container while every unit test stays green).
/// </summary>
public sealed class DistributionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Image_copies_repo_config_for_vocabulary_packs()
    {
        string dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot, "docker", "Dockerfile"));
        Assert.Contains("COPY config", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_context_does_not_exclude_repo_config()
    {
        string[] lines = [.. File.ReadAllLines(Path.Combine(RepositoryRoot, ".dockerignore"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

        Assert.DoesNotContain("config", lines);
        Assert.DoesNotContain("config/**", lines);
        Assert.DoesNotContain("**/*.json", lines);
    }

    [Fact]
    public void Every_repo_pack_loads()
    {
        string directory = Path.Combine(RepositoryRoot, "config", "knowledge");
        Assert.True(Directory.Exists(directory), $"missing repo pack directory {directory}");

        // Committing a repo-owned pack here is the documented first pack location, so the
        // guard checks that each one still loads rather than forbidding *.json.
        foreach (string? pack in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var catalog = KnowledgeVocabularyLoader.Load([pack]);
            Assert.Contains(
                catalog.NodeKinds.Concat<object>(catalog.EdgeTypes),
                entry => entry is NodeKindDefinition nodeKind && nodeKind.Source != KnowledgeVocabularyCatalog.CorePack
                    || entry is EdgeTypeDefinition edgeType && edgeType.Source != KnowledgeVocabularyCatalog.CorePack);
        }
    }

    [Fact]
    public void Shipped_example_pack_still_loads()
    {
        string path = Path.Combine(RepositoryRoot, "examples", "packs", "example-vocabulary.json");
        var catalog = KnowledgeVocabularyLoader.Load([path]);

        var kind = catalog.RequireKind("example:invoice");
        Assert.Equal(["repo"], kind.RequiredProps);
        Assert.Equal("example", kind.Source);

        var edgeType = catalog.RequireEdgeType("example:billed_to");
        Assert.Equal("example:billed_from", edgeType.InverseName);
        Assert.True(edgeType.Acyclic);
        Assert.True(catalog.AllowsPair("example:billed_to", "example:invoice", KnowledgeKinds.Repo));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Anima.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not locate the repository root (Anima.sln)");
    }
}
