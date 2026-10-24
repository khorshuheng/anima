using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Core.Tests;

public sealed class InMemoryKnowledgeGraphTests
{
    [Fact]
    public async Task Plan_wires_tasks_and_resolution_is_derived_from_implements()
    {
        var graph = CreateGraph();
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));

        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Ship the token refresh path",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("schema", "Refresh token schema"),
                new PlanTaskDraft("endpoint", "Add refresh endpoint", DependsOn: ["schema"]),
            ]));

        Assert.NotNull(plan.Goal);
        Assert.Equal(2, plan.Tasks.Count);

        var endpoint = plan.Tasks.Single(task => task.Key == "endpoint").Node;
        var schema = plan.Tasks.Single(task => task.Key == "schema").Node;

        var blockers = await graph.GetNeighborsAsync(
            new NeighborQuery(endpoint.Id, EdgeDirection.Outgoing, KnowledgeEdgeTypes.DependsOn));
        Assert.Single(blockers);
        Assert.Equal(schema.Id, blockers[0].Node.Id);

        var activeTasks = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Task,
            Scope = KnowledgeScope.Active,
        });
        Assert.Equal(2, activeTasks.Total);

        var change = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Change, "Add refresh handler"));
        await graph.AddEdgeAsync(new EdgeDraft(change.Id, endpoint.Id, KnowledgeEdgeTypes.Implements));

        var resolved = await graph.GetNodeAsync(endpoint.Id);
        Assert.NotNull(resolved);
        Assert.True(resolved.Resolved);
        Assert.False((await graph.GetNodeAsync(schema.Id))!.Resolved);

        var stillActive = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Task,
            Scope = KnowledgeScope.Active,
        });
        Assert.Equal(1, stillActive.Total);
    }

    [Fact]
    public async Task Single_outgoing_edges_are_enforced()
    {
        var graph = CreateGraph();
        var firstRepo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var secondRepo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "other"));

        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "A goal",
            Anchor: new PlanAnchor(firstRepo.Id, KnowledgeEdgeTypes.BelongsTo)));

        Assert.NotNull(plan.Goal);

        var exception = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(plan.Goal.Id, secondRepo.Id, KnowledgeEdgeTypes.BelongsTo)));

        Assert.Contains("one outgoing edge", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reanchoring_an_existing_goal_is_applied_or_rejected()
    {
        var graph = CreateGraph();
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
    }

    [Fact]
    public async Task Task_creation_requires_an_anchored_parent()
    {
        var graph = CreateGraph();
        var looseGoal = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Goal, "Loose goal"));

        await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateTaskAsync(new TaskDraft("Orphan", Parent: looseGoal.Id)));

        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Anchored goal",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo)));

        var task = await graph.CreateTaskAsync(new TaskDraft("Grounded", Parent: plan.Goal!.Id));
        Assert.Equal(KnowledgeKinds.Task, task.Kind);
    }

    [Fact]
    public async Task Search_ranks_and_filters_by_kind()
    {
        var graph = CreateGraph();
        await graph.CreateNodeAsync(new NodeDraft(
            KnowledgeKinds.Memory,
            "UAT login flow",
            Content: "The login page refresh token is stored in localStorage."));
        await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Decision, "Logging policy"));

        var page = await graph.SearchAsync(
            "refresh token",
            new NodeQuery { Kind = KnowledgeKinds.Memory, Scope = KnowledgeScope.All });

        Assert.Equal(1, page.Total);
        Assert.Equal("UAT login flow", page.Items[0].Name);
    }

    [Fact]
    public async Task Scoping_walks_anchor_then_descendants()
    {
        var graph = CreateGraph();
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Goal",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks: [new PlanTaskDraft("one", "First task")]));

        var scoped = await graph.QueryNodesAsync(new NodeQuery
        {
            Within = repo.Id,
            Via = KnowledgeEdgeTypes.BelongsTo,
            Descend = KnowledgeEdgeTypes.PartOf,
            Scope = KnowledgeScope.All,
        });

        Assert.Equal(2, scoped.Total);
        Assert.Contains(scoped.Items, node => node.Id == plan.Goal!.Id);
        Assert.Contains(scoped.Items, node => node.Id == plan.Tasks[0].Node.Id);
    }

    [Fact]
    public async Task Graph_snapshot_orders_by_id_and_enforces_caps()
    {
        var graph = CreateGraph();
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Goal",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("api", "Graph API"),
                new PlanTaskDraft("ui", "Graph tab", DependsOn: ["api"]),
            ]));

        var all = await graph.GetGraphAsync(new GraphSnapshotQuery());

        Assert.False(all.Truncated);
        Assert.Equal(4, all.NodeCount);
        Assert.Equal(4, all.EdgeCount);
        Assert.Equal(
            new[] { repo.Id, plan.Goal!.Id, plan.Tasks[0].Node.Id, plan.Tasks[1].Node.Id },
            all.Nodes.Select(node => node.Id));

        var capped = await graph.GetGraphAsync(new GraphSnapshotQuery { MaxNodes = 2, MaxEdges = 1 });

        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Nodes.Count);
        Assert.Single(capped.Edges);
        Assert.All(capped.Edges, edge => Assert.Contains(edge.Source, capped.Nodes.Select(node => node.Id)));
        Assert.All(capped.Edges, edge => Assert.Contains(edge.Destination, capped.Nodes.Select(node => node.Id)));
    }

    [Fact]
    public async Task Graph_snapshot_newest_windows_the_graph_from_the_highest_ids()
    {
        var graph = CreateGraph();
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

        var all = await graph.GetGraphAsync(new GraphSnapshotQuery { Newest = true });

        Assert.Equal(new[] { ui.Id, api.Id, plan.Goal!.Id, repo.Id }, all.Nodes.Select(node => node.Id));
        Assert.Equal(
            all.Edges.Select(edge => edge.Id).OrderDescending(),
            all.Edges.Select(edge => edge.Id));

        var window = await graph.GetGraphAsync(new GraphSnapshotQuery { Newest = true, MaxNodes = 2 });

        Assert.True(window.Truncated);
        Assert.Equal(new[] { ui.Id, api.Id }, window.Nodes.Select(node => node.Id));
        Assert.Equal(4, window.NodeCount);
        var edge = Assert.Single(window.Edges);
        Assert.Equal((ui.Id, api.Id, KnowledgeEdgeTypes.DependsOn), (edge.Source, edge.Destination, edge.EdgeType));
    }

    [Fact]
    public async Task Graph_snapshot_since_id_filters_to_newer_nodes()
    {
        var graph = CreateGraph();
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var plan = await graph.CreatePlanAsync(new PlanDraft(
            "Goal",
            Anchor: new PlanAnchor(repo.Id, KnowledgeEdgeTypes.BelongsTo),
            Tasks:
            [
                new PlanTaskDraft("api", "Graph API"),
                new PlanTaskDraft("ui", "Graph tab", DependsOn: ["api"]),
            ]));
        var goal = plan.Goal!;

        var delta = await graph.GetGraphAsync(new GraphSnapshotQuery { SinceId = goal.Id });

        Assert.Equal(
            new[] { plan.Tasks[0].Node.Id, plan.Tasks[1].Node.Id },
            delta.Nodes.Select(node => node.Id));
        Assert.Equal(4, delta.NodeCount);
        Assert.True(delta.Truncated);
        Assert.Contains(
            delta.Edges,
            edge => edge.Source == plan.Tasks[1].Node.Id &&
                    edge.Destination == plan.Tasks[0].Node.Id &&
                    edge.EdgeType == KnowledgeEdgeTypes.DependsOn);
        Assert.DoesNotContain(delta.Edges, edge => edge.Source == goal.Id || edge.Destination == goal.Id);
    }

    private static InMemoryKnowledgeGraph CreateGraph() => new(
        Options.Create(new KnowledgeOptions()),
        NullLogger<InMemoryKnowledgeGraph>.Instance);
}

public sealed class KnowledgeUpdateTests
{
    [Fact]
    public async Task Update_node_replaces_properties_and_content()
    {
        var graph = CreateGraph();
        var node = await graph.CreateNodeAsync(new NodeDraft(
            KnowledgeKinds.Workflow,
            "Workflow",
            Properties: new Dictionary<string, string> { ["status"] = "analysis" }));

        var updated = await graph.UpdateNodeAsync(
            node.Id,
            new NodePatch(
                Description: "updated",
                Content: "body",
                Properties: new Dictionary<string, string> { ["status"] = "specReady" }));

        Assert.Equal("updated", updated.Description);
        Assert.Equal("body", updated.Content);
        Assert.Equal("specReady", updated.Properties["status"]);
    }

    [Fact]
    public async Task Query_filters_by_properties_and_can_omit_content()
    {
        var graph = CreateGraph();
        await graph.CreateNodeAsync(new NodeDraft(
            KnowledgeKinds.Spec,
            "Spec A",
            Content: "long markdown",
            Properties: new Dictionary<string, string> { ["workflowId"] = "7" }));
        await graph.CreateNodeAsync(new NodeDraft(
            KnowledgeKinds.Spec,
            "Spec B",
            Content: "other",
            Properties: new Dictionary<string, string> { ["workflowId"] = "8" }));

        var page = await graph.QueryNodesAsync(new NodeQuery
        {
            Kind = KnowledgeKinds.Spec,
            Scope = KnowledgeScope.All,
            SummaryOnly = true,
            Properties = new Dictionary<string, string> { ["workflowId"] = "7" },
        });

        Assert.Equal(1, page.Total);
        Assert.Equal("Spec A", page.Items[0].Name);
        Assert.Equal(string.Empty, page.Items[0].Content);
        Assert.Equal("7", page.Items[0].Properties["workflowId"]);
    }

    private static InMemoryKnowledgeGraph CreateGraph() => new(
        Options.Create(new KnowledgeOptions()),
        NullLogger<InMemoryKnowledgeGraph>.Instance);
}
