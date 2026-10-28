using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Sqlite.Tests;

/// <summary>
/// The SQLite store resolves trait sets from the vocabulary into JSON parameters
/// (<c>json_each</c>), so a pack can add scoping vocabulary without touching SQL.
/// </summary>
public sealed class SqliteVocabularyTraitTests : IDisposable
{
    private const string Epic = "acme:epic";
    private const string EpicAnchored = "acme:epic_anchored";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "anima-traits-" + Guid.NewGuid().ToString("n"));

    public SqliteVocabularyTraitTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Pack_traits_drive_unanchored_health_and_task_parents()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"), PackCatalog());

        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var epic = await graph.CreateNodeAsync(new NodeDraft(Epic, "Refresh tokens"));
        var task = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Task, "Rotate tokens"));
        await graph.CreateNodeAsync(new NodeDraft("acme:invoice", "Not plannable"));

        await graph.AddEdgeAsync(new EdgeDraft(task.Id, epic.Id, KnowledgeEdgeTypes.PartOf));

        Assert.Equal(2, (await graph.GetHealthAsync()).UnanchoredWork);

        var notAnchored = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateTaskAsync(new TaskDraft("child", Parent: epic.Id)));
        Assert.Contains("not anchored", notAnchored.Message, StringComparison.Ordinal);

        await graph.AddEdgeAsync(new EdgeDraft(epic.Id, repo.Id, EpicAnchored));

        Assert.Equal(0, (await graph.GetHealthAsync()).UnanchoredWork);
        var allowed = await graph.CreateTaskAsync(new TaskDraft("child", Parent: epic.Id));
        Assert.Equal(KnowledgeKinds.Task, allowed.Kind);
    }

    [Fact]
    public async Task Without_the_pack_the_core_vocabulary_still_anchors_plans()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"), KnowledgeVocabularyLoader.LoadCore());

        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        await graph.CreatePlanAsync(new PlanDraft(
            "Ship refresh tokens",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks: [new PlanTaskDraft("schema", "Refresh token schema")]));

        Assert.Equal(0, (await graph.GetHealthAsync()).UnanchoredWork);
    }

    private static KnowledgeVocabularyCatalog PackCatalog() => KnowledgeVocabularyLoader.Build(
        KnowledgeVocabularyLoader.LoadEmbeddedCore(),
        [
            new VocabularyPack
            {
                Name = "acme",
                NodeKinds =
                [
                    new NodeKindSpec { Name = Epic, Traits = [KnowledgeTraits.Plannable] },
                    new NodeKindSpec { Name = "acme:invoice" },
                ],
                EdgeTypes =
                [
                    new EdgeTypeSpec
                    {
                        Name = EpicAnchored,
                        Pairs = [new EdgeKindPair(Epic, KnowledgeKinds.Repo)],
                        Traits = [KnowledgeTraits.Anchor],
                    },
                    new EdgeTypeSpec
                    {
                        Name = KnowledgeEdgeTypes.PartOf,
                        SourceKinds = [KnowledgeKinds.Task],
                        DestinationKinds = [Epic],
                    },
                ],
            },
        ]);

    private static SqliteKnowledgeGraph CreateGraph(string path, KnowledgeVocabularyCatalog vocabulary) => new(
        Options.Create(new SqliteKnowledgeOptions
        {
            DatabasePath = path,
            BusyTimeoutSeconds = 5,
        }),
        Options.Create(new KnowledgeOptions()),
        NullLogger<SqliteKnowledgeGraph>.Instance,
        vocabulary);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
