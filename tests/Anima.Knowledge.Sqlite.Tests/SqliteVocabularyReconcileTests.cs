using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Sqlite.Tests;

/// <summary>
/// Vocabulary provenance and reconcile behaviour: ad-hoc kinds share the pack id band,
/// a pack that later formalizes one keeps the same id, and a pack that disappears
/// deactivates its unused rows or fails startup when the graph still uses them.
/// </summary>
public sealed class SqliteVocabularyReconcileTests : IDisposable
{
    private const string Epic = "acme:epic";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "anima-reconcile-" + Guid.NewGuid().ToString("n"));

    private readonly string _path;

    public SqliteVocabularyReconcileTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "knowledge.db");
    }

    [Fact]
    public async Task Ad_hoc_kind_and_pack_kind_agree_on_id()
    {
        long adHocId;
        using (var defaultGraph = CreateGraph(KnowledgeVocabularyLoader.LoadCore()))
        {
            await defaultGraph.CreateNodeAsync(new NodeDraft(Epic, "written before the pack"));
            var epic = (await defaultGraph.GetKindsAsync()).Single(kind => kind.Name == Epic);
            Assert.Equal("ad-hoc", epic.Source);
            Assert.True(epic.Id >= KnowledgeVocabularyCatalog.PackIdFloor);
            adHocId = epic.Id;
        }

        var catalog = PackCatalog();
        using var graph = CreateGraph(catalog);
        var fromDb = (await graph.GetKindsAsync()).Single(kind => kind.Name == Epic);

        Assert.Equal(catalog.RequireKind(Epic).Id, fromDb.Id);
        Assert.Equal(adHocId, fromDb.Id);
        Assert.Equal("acme", fromDb.Source);
        Assert.Equal(new[] { KnowledgeTraits.Plannable }, fromDb.Traits.ToArray());
    }

    [Fact]
    public async Task Pack_kind_that_disappears_is_deactivated_when_unused()
    {
        // A first boot loads the pack so its rows exist; the next boot does not.
        CreateGraph(PackCatalog()).Dispose();

        using var coreOnly = CreateGraph(KnowledgeVocabularyLoader.LoadCore());

        Assert.DoesNotContain(Epic, (await coreOnly.GetKindsAsync()).Select(kind => kind.Name));
        Assert.Equal(1, InactiveEpicRows());
    }

    [Fact]
    public async Task Pack_kind_that_disappears_and_is_in_use_fails_startup()
    {
        using (var packed = CreateGraph(PackCatalog()))
        {
            await packed.CreateNodeAsync(new NodeDraft(
                Epic,
                "still referenced",
                Properties: new Dictionary<string, string> { ["repo"] = "anima" }));
        }

        var exception = Assert.Throws<KnowledgeValidationException>(
            () => CreateGraph(KnowledgeVocabularyLoader.LoadCore()));

        Assert.Contains("no longer defines", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Epic, exception.Message, StringComparison.Ordinal);
        Assert.Contains("still uses", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deactivated_pack_kind_is_not_writable()
    {
        CreateGraph(PackCatalog()).Dispose();

        using var coreOnly = CreateGraph(KnowledgeVocabularyLoader.LoadCore());
        var exception = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => coreOnly.CreateNodeAsync(new NodeDraft(Epic, "resurrected")));
        Assert.Contains("inactive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_properties_are_enforced_for_pack_kinds()
    {
        using var graph = CreateGraph(PackCatalog());

        var missing = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateNodeAsync(new NodeDraft(Epic, "no props")));
        Assert.Contains("requires properties repo", missing.Message, StringComparison.Ordinal);

        var created = await graph.CreateNodeAsync(new NodeDraft(
            Epic,
            "with props",
            Properties: new Dictionary<string, string> { ["repo"] = "anima" }));
        Assert.Equal(Epic, created.Kind);
    }

    [Fact]
    public async Task Sqlite_rejects_cycles_and_mirror_writes_like_the_memory_store()
    {
        using var graph = CreateGraph(PackCatalog());
        var first = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Task, "first"));
        var second = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Task, "second"));

        await graph.AddEdgeAsync(new EdgeDraft(first.Id, second.Id, KnowledgeEdgeTypes.DependsOn));
        var cycle = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(second.Id, first.Id, KnowledgeEdgeTypes.DependsOn)));
        Assert.Contains("would close a cycle", cycle.Message, StringComparison.Ordinal);

        var alice = await graph.CreateNodeAsync(new NodeDraft("acme:person", "alice"));
        var bob = await graph.CreateNodeAsync(new NodeDraft("acme:person", "bob"));
        var forward = await graph.AddEdgeAsync(new EdgeDraft(alice.Id, bob.Id, "acme:knows"));
        var mirror = await graph.AddEdgeAsync(new EdgeDraft(bob.Id, alice.Id, "acme:knows"));
        Assert.Equal(forward.Id, mirror.Id);
        Assert.Single(await graph.GetNeighborsAsync(new NeighborQuery(alice.Id, EdgeDirection.Both, "acme:knows")));
    }

    [Fact]
    public async Task Deprecated_kind_is_hidden_from_listings_but_still_writable()
    {
        var catalog = KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [
                new VocabularyPack
                {
                    Name = "acme",
                    NodeKinds = [new NodeKindSpec { Name = "acme:legacy", Deprecated = true }],
                },
            ]);

        using var graph = CreateGraph(catalog);

        Assert.DoesNotContain("acme:legacy", (await graph.GetKindsAsync()).Select(kind => kind.Name));
        var node = await graph.CreateNodeAsync(new NodeDraft("acme:legacy", "old row"));
        Assert.Equal("acme:legacy", node.Kind);
    }

    private long InactiveEpicRows()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM node_kind WHERE name = 'acme:epic' AND active = 0;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private SqliteKnowledgeGraph CreateGraph(KnowledgeVocabularyCatalog vocabulary) => new(
        Options.Create(new SqliteKnowledgeOptions { DatabasePath = _path, BusyTimeoutSeconds = 5 }),
        Options.Create(new KnowledgeOptions()),
        NullLogger<SqliteKnowledgeGraph>.Instance,
        vocabulary);

    private static KnowledgeVocabularyCatalog PackCatalog() => KnowledgeVocabularyLoader.Build(
        KnowledgeVocabularyLoader.LoadEmbeddedCore(),
        [
            new VocabularyPack
            {
                Name = "acme",
                NodeKinds =
                [
                    new NodeKindSpec { Name = Epic, Traits = [KnowledgeTraits.Plannable], RequiredProps = ["repo"] },
                    new NodeKindSpec { Name = "acme:person" },
                ],
                EdgeTypes =
                [
                    new EdgeTypeSpec { Name = "acme:knows", Pairs = [new EdgeKindPair("acme:person", "acme:person")], Symmetric = true },
                ],
            },
        ]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
