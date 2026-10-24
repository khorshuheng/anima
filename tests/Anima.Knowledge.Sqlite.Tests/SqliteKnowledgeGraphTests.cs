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
    public async Task Page_totals_remain_correct_when_offset_is_past_the_end()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        for (var index = 0; index < 2; index++)
        {
            await graph.CreateNodeAsync(new NodeDraft(
                KnowledgeKinds.Memory,
                $"probe {index}",
                Content: "searchable token"));
        }

        var page = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Memory,
            Scope = KnowledgeScope.All,
            Limit = 10,
            Offset = 10,
        });
        var search = await graph.SearchAsync("searchable", new NodeQuery
        {
            Scope = KnowledgeScope.All,
            Limit = 10,
            Offset = 10,
        });

        Assert.Empty(page.Items);
        Assert.Equal(2, page.Total);
        Assert.Empty(search.Items);
        Assert.Equal(2, search.Total);
    }

    [Fact]
    public async Task Reanchoring_an_existing_goal_is_applied_or_rejected()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repoOne = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "repo-one"));
        var repoTwo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "repo-two"));
        var anchored = await graph.CreatePlanAsync(new PlanDraft(
            "Anchored",
            Anchor: new PlanAnchor(repoOne.Id, KnowledgeEdgeTypes.BelongsTo)));

        await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreatePlanAsync(new PlanDraft(
                "Re-anchor",
                GoalId: anchored.Goal!.Id,
                Anchor: new PlanAnchor(repoTwo.Id, KnowledgeEdgeTypes.BelongsTo))));

        var same = await graph.CreatePlanAsync(new PlanDraft(
            "Same anchor",
            GoalId: anchored.Goal!.Id,
            Anchor: new PlanAnchor(repoOne.Id, KnowledgeEdgeTypes.BelongsTo)));
        Assert.Equal(anchored.Goal!.Id, same.Goal!.Id);

        var loose = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Goal, "Loose"));
        await graph.CreatePlanAsync(new PlanDraft(
            "Attach loose",
            GoalId: loose.Id,
            Anchor: new PlanAnchor(repoTwo.Id, KnowledgeEdgeTypes.BelongsTo)));
        var neighbors = await graph.GetNeighborsAsync(
            new NeighborQuery(loose.Id, EdgeDirection.Outgoing, KnowledgeEdgeTypes.BelongsTo));
        Assert.Contains(neighbors, neighbor => neighbor.Node.Id == repoTwo.Id);
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

    [Fact]
    public async Task Graph_snapshot_returns_ordered_summaries_with_totals()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima", "the repo"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Ship the dashboard",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("api", "Graph API", "snapshot endpoint"),
                new PlanTaskDraft("ui", "Graph tab", DependsOn: ["api"]),
            ]));
        var goal = plan.Goal!;
        var api = plan.Tasks.Single(task => task.Key == "api").Node;
        var ui = plan.Tasks.Single(task => task.Key == "ui").Node;
        var change = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Change, "Ship it"));
        await graph.AddEdgeAsync(new EdgeDraft(change.Id, api.Id, KnowledgeEdgeTypes.Implements));

        var snapshot = await graph.GetGraphAsync(new GraphSnapshotQuery());

        Assert.False(snapshot.Truncated);
        Assert.Equal(5, snapshot.NodeCount);
        Assert.Equal(5, snapshot.EdgeCount);
        Assert.Equal(
            new[] { repo.Id, goal.Id, api.Id, ui.Id, change.Id },
            snapshot.Nodes.Select(node => node.Id));
        Assert.Equal(
            snapshot.Edges.Select(edge => edge.Id).Order(),
            snapshot.Edges.Select(edge => edge.Id));
        Assert.Equal(5, snapshot.Edges.Count);
        Assert.Equal("snapshot endpoint", snapshot.Nodes.Single(node => node.Id == api.Id).Description);
        Assert.True(snapshot.Nodes.Single(node => node.Id == api.Id).Resolved);
        Assert.False(snapshot.Nodes.Single(node => node.Id == goal.Id).Resolved);
        Assert.Contains(
            snapshot.Edges,
            edge => edge.Source == change.Id &&
                    edge.Destination == api.Id &&
                    edge.EdgeType == KnowledgeEdgeTypes.Implements);
    }

    [Fact]
    public async Task Graph_snapshot_caps_nodes_and_edges_and_keeps_only_internal_edges()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Goal",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("api", "Graph API"),
                new PlanTaskDraft("ui", "Graph tab", DependsOn: ["api"]),
            ]));
        var change = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Change, "Ship it"));
        await graph.AddEdgeAsync(
            new EdgeDraft(change.Id, plan.Tasks.Single(task => task.Key == "api").Node.Id, KnowledgeEdgeTypes.Implements));

        var nodeCapped = await graph.GetGraphAsync(new GraphSnapshotQuery { MaxNodes = 2, MaxEdges = 10 });

        Assert.True(nodeCapped.Truncated);
        Assert.Equal(5, nodeCapped.NodeCount);
        Assert.Equal(5, nodeCapped.EdgeCount);
        Assert.Equal(new[] { repo.Id, plan.Goal!.Id }, nodeCapped.Nodes.Select(node => node.Id));
        Assert.Single(nodeCapped.Edges);
        Assert.Equal(KnowledgeEdgeTypes.BelongsTo, nodeCapped.Edges[0].EdgeType);

        var singleNode = await graph.GetGraphAsync(new GraphSnapshotQuery { MaxNodes = 1, MaxEdges = 10 });
        Assert.Single(singleNode.Nodes);
        Assert.Empty(singleNode.Edges);
        Assert.True(singleNode.Truncated);

        var edgeCapped = await graph.GetGraphAsync(new GraphSnapshotQuery { MaxNodes = 10, MaxEdges = 2 });
        Assert.Equal(5, edgeCapped.Nodes.Count);
        Assert.Equal(2, edgeCapped.Edges.Count);
        Assert.True(edgeCapped.Truncated);
        Assert.Equal(
            edgeCapped.Edges.Select(edge => edge.Id).Order(),
            edgeCapped.Edges.Select(edge => edge.Id));
    }

    [Fact]
    public async Task Graph_snapshot_clamps_caps_to_the_supported_range()
    {
        var minimum = new GraphSnapshotQuery { MaxNodes = 0, MaxEdges = -5 };
        var maximum = new GraphSnapshotQuery { MaxNodes = 99_999, MaxEdges = 99_999 };

        Assert.Equal(GraphSnapshotQuery.MinCap, minimum.EffectiveMaxNodes);
        Assert.Equal(GraphSnapshotQuery.MinCap, minimum.EffectiveMaxEdges);
        Assert.Equal(GraphSnapshotQuery.MaxCap, maximum.EffectiveMaxNodes);
        Assert.Equal(GraphSnapshotQuery.MaxCap, maximum.EffectiveMaxEdges);
        Assert.Equal(500, new GraphSnapshotQuery().EffectiveMaxNodes);
        Assert.Equal(1000, new GraphSnapshotQuery().EffectiveMaxEdges);

        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var goal = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Goal, "Loose"));
        await graph.AddEdgeAsync(new EdgeDraft(goal.Id, repo.Id, KnowledgeEdgeTypes.BelongsTo));

        var clamped = await graph.GetGraphAsync(new GraphSnapshotQuery { MaxNodes = 0, MaxEdges = 0 });

        Assert.Single(clamped.Nodes);
        Assert.Equal(repo.Id, clamped.Nodes[0].Id);
        Assert.Empty(clamped.Edges);
        Assert.True(clamped.Truncated);
    }

    [Fact]
    public async Task Graph_snapshot_newest_uses_one_descending_window_for_nodes_and_edges()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Goal",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("api", "Graph API"),
                new PlanTaskDraft("ui", "Graph tab", DependsOn: ["api"]),
            ]));
        var api = plan.Tasks.Single(task => task.Key == "api").Node;
        var ui = plan.Tasks.Single(task => task.Key == "ui").Node;
        var change = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Change, "Ship it"));
        await graph.AddEdgeAsync(new EdgeDraft(change.Id, api.Id, KnowledgeEdgeTypes.Implements));

        var all = await graph.GetGraphAsync(new GraphSnapshotQuery { Newest = true });

        Assert.Equal(new[] { change.Id, ui.Id, api.Id, plan.Goal!.Id, repo.Id }, all.Nodes.Select(node => node.Id));
        Assert.Equal(
            all.Edges.Select(edge => edge.Id).OrderDescending(),
            all.Edges.Select(edge => edge.Id));

        // The window holds the three newest nodes; edges that reach the older nodes
        // (ui -> goal) stay out even though they exist in the graph.
        var window = await graph.GetGraphAsync(new GraphSnapshotQuery { Newest = true, MaxNodes = 3 });

        Assert.True(window.Truncated);
        Assert.Equal(new[] { change.Id, ui.Id, api.Id }, window.Nodes.Select(node => node.Id));
        Assert.Equal(5, window.NodeCount);
        Assert.All(window.Edges, edge =>
        {
            Assert.Contains(edge.Source, window.Nodes.Select(node => node.Id));
            Assert.Contains(edge.Destination, window.Nodes.Select(node => node.Id));
        });
        Assert.Equal(
            new[] { KnowledgeEdgeTypes.Implements, KnowledgeEdgeTypes.DependsOn },
            window.Edges.Select(edge => edge.EdgeType));
    }

    [Fact]
    public async Task Graph_snapshot_since_id_filters_to_newer_nodes()
    {
        using var graph = CreateGraph(Path.Combine(_directory, "knowledge.db"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var goal = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Goal, "Goal"));
        await graph.AddEdgeAsync(new EdgeDraft(goal.Id, repo.Id, KnowledgeEdgeTypes.BelongsTo));
        var task = await graph.CreateTaskAsync(new TaskDraft("Graph API", Parent: goal.Id));
        var change = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Change, "Ship it"));
        await graph.AddEdgeAsync(new EdgeDraft(change.Id, task.Id, KnowledgeEdgeTypes.Implements));

        var delta = await graph.GetGraphAsync(new GraphSnapshotQuery { SinceId = goal.Id });

        Assert.Equal(new[] { task.Id, change.Id }, delta.Nodes.Select(node => node.Id));
        Assert.Equal(4, delta.NodeCount);
        Assert.True(delta.Truncated);
        var edge = Assert.Single(delta.Edges);
        Assert.Equal((change.Id, task.Id, KnowledgeEdgeTypes.Implements),
            (edge.Source, edge.Destination, edge.EdgeType));
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
