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

    private static InMemoryKnowledgeGraph CreateGraph() => new(
        Options.Create(new KnowledgeOptions()),
        NullLogger<InMemoryKnowledgeGraph>.Instance);
}
