using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Sqlite.Tests;

public sealed class SqliteKnowledgeGraphTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "anima-sqlite-" + Guid.NewGuid().ToString("n"));

    public SqliteKnowledgeGraphTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Nodes_and_properties_survive_a_new_process_instance()
    {
        var path = Path.Combine(_directory, "knowledge.db");
        using (var first = CreateGraph(path))
        {
            var node = await first.CreateNodeAsync(new NodeDraft(
                KnowledgeKinds.Workflow,
                "Persisted workflow",
                "survives restart",
                "body",
                new Dictionary<string, string> { ["status"] = "analysis", ["repoPath"] = "/tmp/repo" }));
            Assert.True(node.Id > 0);
        }

        using var second = CreateGraph(path);
        var loaded = await second.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Workflow,
            Scope = KnowledgeScope.All,
        });

        Assert.Single(loaded.Items);
        Assert.Equal("Persisted workflow", loaded.Items[0].Name);
        Assert.Equal("analysis", loaded.Items[0].Properties["status"]);
        Assert.Equal("body", loaded.Items[0].Content);
    }

    [Fact]
    public async Task Plans_resolution_scoping_and_fts_search_work()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Ship refresh tokens",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("schema", "Refresh token schema", Content: "rotation and revocation"),
                new PlanTaskDraft("endpoint", "Add refresh endpoint", DependsOn: ["schema"]),
            ]));

        Assert.NotNull(plan.Goal);
        var endpoint = plan.Tasks.Single(task => task.Key == "endpoint").Node;

        var search = await graph.SearchAsync(
            "refresh",
            new NodeQuery { Kind = KnowledgeKinds.Task, Scope = KnowledgeScope.All });
        Assert.Equal(2, search.Total);

        var scoped = await graph.QueryNodesAsync(new NodeQuery
        {
            Within = repo.Id,
            Via = KnowledgeEdgeTypes.BelongsTo,
            Descend = KnowledgeEdgeTypes.PartOf,
            Scope = KnowledgeScope.All,
        });
        Assert.Equal(3, scoped.Total);

        var change = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Change, "Refresh handler"));
        await graph.AddEdgeAsync(new EdgeDraft(change.Id, endpoint.Id, KnowledgeEdgeTypes.Implements));

        Assert.True((await graph.GetNodeAsync(endpoint.Id))!.Resolved);
        var active = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Task,
            Scope = KnowledgeScope.Active,
        });
        Assert.Equal(1, active.Total);
    }

    [Fact]
    public async Task Updates_replace_properties_and_summary_omits_content()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var node = await graph.CreateNodeAsync(new NodeDraft(
            KnowledgeKinds.Workflow,
            "Workflow",
            Content: "large content",
            Properties: new Dictionary<string, string> { ["status"] = "analysis" }));

        var updated = await graph.UpdateNodeAsync(
            node.Id,
            new NodePatch(
                Content: "updated content",
                Properties: new Dictionary<string, string> { ["status"] = "specReady" }));

        Assert.Equal("specReady", updated.Properties["status"]);
        Assert.Equal("updated content", updated.Content);

        var summary = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Workflow,
            Scope = KnowledgeScope.All,
            SummaryOnly = true,
            Properties = new Dictionary<string, string> { ["status"] = "specReady" },
        });

        Assert.Single(summary.Items);
        Assert.Equal(string.Empty, summary.Items[0].Content);
    }

    [Fact]
    public async Task Edge_declarations_and_single_outgoing_are_enforced()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repoA = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "a"));
        var repoB = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "b"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Goal",
            Anchor: new PlanAnchor(repoA.Id, KnowledgeEdgeTypes.BelongsTo)));

        await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(plan.Goal!.Id, repoB.Id, KnowledgeEdgeTypes.BelongsTo)));

        await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(repoA.Id, plan.Goal!.Id, KnowledgeEdgeTypes.BelongsTo)));
    }

    [Fact]
    public async Task Concurrent_writes_are_serialized()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var tasks = Enumerable.Range(0, 25)
            .Select(index => graph.CreateNodeAsync(new NodeDraft(
                KnowledgeKinds.Memory,
                $"memory {index}",
                Properties: new Dictionary<string, string> { ["i"] = index.ToString(CultureInfo.InvariantCulture) })))
            .ToArray();

        await Task.WhenAll(tasks);

        var page = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Memory,
            Scope = KnowledgeScope.All,
            Limit = 100,
        });
        Assert.Equal(25, page.Total);
    }

    [Fact]
    public async Task Task_creation_requires_an_anchored_parent()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var loose = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Goal, "Loose"));

        await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateTaskAsync(new TaskDraft("Orphan", Parent: loose.Id)));
    }

    private static SqliteKnowledgeGraph CreateGraph(string path) => new(
        Options.Create(new SqliteKnowledgeOptions
        {
            DatabasePath = path,
            BusyTimeoutSeconds = 5,
        }),
        Options.Create(new KnowledgeOptions()),
        NullLogger<SqliteKnowledgeGraph>.Instance);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
