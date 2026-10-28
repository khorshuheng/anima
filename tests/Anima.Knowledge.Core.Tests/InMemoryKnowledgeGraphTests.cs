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

        Assert.NotNull(plan.Goal!);
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

        Assert.NotNull(plan.Goal!);

        var exception = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(plan.Goal!.Id, secondRepo.Id, KnowledgeEdgeTypes.BelongsTo)));

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
                Anchor: new PlanAnchor(repoTwo.Id, KnowledgeEdgeTypes.BelongsTo),
                GoalId: anchored.Goal!.Id)));

        var same = await graph.CreatePlanAsync(new PlanDraft(
            "Same anchor",
            Anchor: new PlanAnchor(repoOne.Id, KnowledgeEdgeTypes.BelongsTo),
            GoalId: anchored.Goal!.Id));
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
            [repo.Id, plan.Goal!.Id, plan.Tasks[0].Node.Id, plan.Tasks[1].Node.Id],
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

        Assert.Equal([ui.Id, api.Id, plan.Goal!.Id, repo.Id], all.Nodes.Select(node => node.Id));
        Assert.Equal(
            all.Edges.Select(edge => edge.Id).OrderDescending(),
            all.Edges.Select(edge => edge.Id));

        var window = await graph.GetGraphAsync(new GraphSnapshotQuery { Newest = true, MaxNodes = 2 });

        Assert.True(window.Truncated);
        Assert.Equal([ui.Id, api.Id], window.Nodes.Select(node => node.Id));
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
            [plan.Tasks[0].Node.Id, plan.Tasks[1].Node.Id],
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

    private static InMemoryKnowledgeGraph CreateGraph(KnowledgeVocabularyCatalog? vocabulary = null) => new(
        Options.Create(new KnowledgeOptions()),
        NullLogger<InMemoryKnowledgeGraph>.Instance,
        vocabulary ?? KnowledgeVocabularyLoader.LoadCore());
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

    private static InMemoryKnowledgeGraph CreateGraph(KnowledgeVocabularyCatalog? vocabulary = null) => new(
        Options.Create(new KnowledgeOptions()),
        NullLogger<InMemoryKnowledgeGraph>.Instance,
        vocabulary ?? KnowledgeVocabularyLoader.LoadCore());
}

/// <summary>
/// Generic graph logic (plan scoping, anchoring, health) keys off vocabulary traits,
/// so a pack can add vocabulary that participates without a code change. A pack that
/// introduces a plannable parent kind must also widen <c>part_of</c>, because the task
/// API keeps writing that edge type.
/// </summary>
public sealed class VocabularyTraitTests
{
    private const string Epic = "acme:epic";
    private const string EpicAnchored = "acme:epic_anchored";

    [Fact]
    public async Task Pack_kind_and_edges_join_scope_logic_through_traits()
    {
        var graph = CreateGraph(PackCatalog());

        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var epic = await graph.CreateNodeAsync(new NodeDraft(Epic, "Refresh tokens"));
        var task = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Task, "Rotate tokens"));
        await graph.CreateNodeAsync(new NodeDraft("acme:invoice", "Not plannable"));

        await graph.AddEdgeAsync(new EdgeDraft(task.Id, epic.Id, KnowledgeEdgeTypes.PartOf));

        // Both plannable nodes are unanchored until the epic has its anchor edge.
        Assert.Equal(2, (await graph.GetHealthAsync()).UnanchoredWork);

        await graph.AddEdgeAsync(new EdgeDraft(epic.Id, repo.Id, EpicAnchored));

        // The task is anchored through the widened hierarchy edge.
        Assert.Equal(0, (await graph.GetHealthAsync()).UnanchoredWork);
    }

    [Fact]
    public async Task Plannable_trait_gates_task_parents()
    {
        var graph = CreateGraph(PackCatalog());

        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var epic = await graph.CreateNodeAsync(new NodeDraft(Epic, "Refresh tokens"));
        var note = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Note, "free form"));

        var notPlannable = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateTaskAsync(new TaskDraft("child", Parent: note.Id)));
        Assert.Contains("plannable kind", notPlannable.Message, StringComparison.Ordinal);

        // The epic is plannable but not anchored yet.
        var notAnchored = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateTaskAsync(new TaskDraft("child", Parent: epic.Id)));
        Assert.Contains("not anchored", notAnchored.Message, StringComparison.Ordinal);

        await graph.AddEdgeAsync(new EdgeDraft(epic.Id, repo.Id, EpicAnchored));
        var allowed = await graph.CreateTaskAsync(new TaskDraft("child", Parent: epic.Id));
        Assert.Equal(KnowledgeKinds.Task, allowed.Kind);
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
                    // Additive: a new anchor edge for the new scope root kind.
                    new EdgeTypeSpec
                    {
                        Name = EpicAnchored,
                        Pairs = [new EdgeKindPair(Epic, KnowledgeKinds.Repo)],
                        Traits = [KnowledgeTraits.Anchor],
                    },
                    // Widening: the task API writes part_of, so the new parent kind
                    // must be an allowed destination.
                    new EdgeTypeSpec
                    {
                        Name = KnowledgeEdgeTypes.PartOf,
                        SourceKinds = [KnowledgeKinds.Task],
                        DestinationKinds = [Epic],
                    },
                ],
            },
        ]);

    private static InMemoryKnowledgeGraph CreateGraph(KnowledgeVocabularyCatalog vocabulary) => new(
        Options.Create(new KnowledgeOptions()),
        NullLogger<InMemoryKnowledgeGraph>.Instance,
        vocabulary);
}

/// <summary>
/// Edges are stored in one canonical direction, but the vocabulary can declare an
/// inverse relation and a symmetric one. Reads accept either name; writes must use
/// the canonical name so the stored direction stays predictable.
/// </summary>
public sealed class EdgeDirectionTests
{
    private const string Invoice = "acme:invoice";
    private const string BilledTo = "acme:billed_to";
    private const string BilledFrom = "acme:billed_from";
    private const string Person = "acme:person";
    private const string Knows = "acme:knows";

    [Fact]
    public async Task Inverse_name_reads_the_stored_edge_from_the_other_end()
    {
        var graph = CreateGraph();
        var invoice = await graph.CreateNodeAsync(new NodeDraft(Invoice, "INV-1"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));

        var stored = await graph.AddEdgeAsync(new EdgeDraft(invoice.Id, repo.Id, BilledTo));

        var outgoing = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(invoice.Id, EdgeDirection.Outgoing, BilledTo)));
        Assert.Equal(repo.Id, outgoing.Node.Id);
        Assert.Equal(EdgeDirection.Outgoing, outgoing.Direction);

        // Named from the other end, the same stored edge is reachable in the opposite role.
        var reversed = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(repo.Id, EdgeDirection.Outgoing, BilledFrom)));
        Assert.Equal(invoice.Id, reversed.Node.Id);
        Assert.Equal(invoice.Id, reversed.Edge.Source);
        Assert.Equal(stored.Id, reversed.Edge.Id);
        Assert.Equal(EdgeDirection.Incoming, reversed.Direction);

        var reversedIncoming = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(invoice.Id, EdgeDirection.Incoming, BilledFrom)));
        Assert.Equal(repo.Id, reversedIncoming.Node.Id);
        Assert.Equal(EdgeDirection.Outgoing, reversedIncoming.Direction);
    }

    [Fact]
    public async Task Inverse_name_on_a_write_is_rejected_with_guidance()
    {
        var graph = CreateGraph();
        var invoice = await graph.CreateNodeAsync(new NodeDraft(Invoice, "INV-1"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));

        var exception = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(repo.Id, invoice.Id, BilledFrom)));

        Assert.Contains("is the inverse of 'acme:billed_to'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symmetric_edge_is_readable_from_both_ends()
    {
        var graph = CreateGraph();
        var alice = await graph.CreateNodeAsync(new NodeDraft(Person, "alice"));
        var bob = await graph.CreateNodeAsync(new NodeDraft(Person, "bob"));

        await graph.AddEdgeAsync(new EdgeDraft(alice.Id, bob.Id, Knows));

        // Stored once as alice -> bob; bob sees it as an outgoing relation too.
        var fromBob = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(bob.Id, EdgeDirection.Outgoing, Knows)));
        Assert.Equal(alice.Id, fromBob.Node.Id);

        var fromAlice = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(alice.Id, EdgeDirection.Incoming, Knows)));
        Assert.Equal(bob.Id, fromAlice.Node.Id);
    }

    [Fact]
    public async Task Inverse_name_works_in_scope_traversals()
    {
        var graph = CreateGraph();
        var invoice = await graph.CreateNodeAsync(new NodeDraft(Invoice, "INV-1"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        await graph.AddEdgeAsync(new EdgeDraft(invoice.Id, repo.Id, BilledTo));

        var page = await graph.QueryNodesAsync(new NodeQuery
        {
            Within = repo.Id,
            Via = BilledFrom,
            Scope = KnowledgeScope.All,
        });

        Assert.Equal(invoice.Id, Assert.Single(page.Items).Id);
    }

    private static InMemoryKnowledgeGraph CreateGraph()
    {
        var catalog = KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [
                new VocabularyPack
                {
                    Name = "acme",
                    NodeKinds =
                    [
                        new NodeKindSpec { Name = Invoice },
                        new NodeKindSpec { Name = Person },
                    ],
                    EdgeTypes =
                    [
                        // The inverse is virtual: one stored edge, readable from both ends.
                        new EdgeTypeSpec
                        {
                            Name = BilledTo,
                            Pairs = [new EdgeKindPair(Invoice, KnowledgeKinds.Repo)],
                            InverseName = BilledFrom,
                        },
                        new EdgeTypeSpec
                        {
                            Name = Knows,
                            Pairs = [new EdgeKindPair(Person, Person)],
                            Symmetric = true,
                        },
                    ],
                },
            ]);

        return new InMemoryKnowledgeGraph(
            Options.Create(new KnowledgeOptions()),
            NullLogger<InMemoryKnowledgeGraph>.Instance,
            catalog);
    }
}

/// <summary>Regressions for the review findings: descend direction, symmetric writes, cycles.</summary>
public sealed class EdgeSemanticsTests
{
    private const string Responds = "acme:responds";
    private const string RespondedBy = "acme:responded_by";
    private const string Person = "acme:person";
    private const string Knows = "acme:knows";

    [Fact]
    public async Task Descend_gives_the_same_result_for_canonical_and_inverse_names()
    {
        var graph = CreateGraph();
        var question = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Question, "q1"));
        var answer = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Answer, "a1"));
        var nested = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Answer, "a2"));
        await graph.AddEdgeAsync(new EdgeDraft(answer.Id, question.Id, Responds));
        await graph.AddEdgeAsync(new EdgeDraft(nested.Id, answer.Id, Responds));

        async Task<string[]> Names(string descend)
        {
            var page = await graph.QueryNodesAsync(new NodeQuery
            {
                Within = question.Id,
                Via = Responds,
                Descend = descend,
                Scope = KnowledgeScope.All,
            });
            return [.. page.Items.Select(item => item.Name).Order(StringComparer.Ordinal)];
        }

        string[] canonical = await Names(Responds);
        Assert.Equal(new[] { "a1", "a2" }, canonical);
        Assert.Equal(canonical, await Names(RespondedBy));
        Assert.Equal(canonical, await Names(RespondedBy.ToUpperInvariant()));
    }

    [Fact]
    public async Task Symmetric_mirror_write_reuses_the_stored_edge()
    {
        var graph = CreateGraph();
        var alice = await graph.CreateNodeAsync(new NodeDraft(Person, "alice"));
        var bob = await graph.CreateNodeAsync(new NodeDraft(Person, "bob"));

        var first = await graph.AddEdgeAsync(new EdgeDraft(alice.Id, bob.Id, Knows));
        var mirror = await graph.AddEdgeAsync(new EdgeDraft(bob.Id, alice.Id, Knows));

        Assert.Equal(first.Id, mirror.Id);
        var neighbors = await graph.GetNeighborsAsync(new NeighborQuery(alice.Id, EdgeDirection.Both, Knows));
        Assert.Single(neighbors);
    }

    [Fact]
    public async Task Acyclic_edge_type_rejects_a_cycle()
    {
        var graph = CreateGraph();
        var first = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Task, "first"));
        var second = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Task, "second"));

        await graph.AddEdgeAsync(new EdgeDraft(first.Id, second.Id, KnowledgeEdgeTypes.DependsOn));

        var exception = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(second.Id, first.Id, KnowledgeEdgeTypes.DependsOn)));
        Assert.Contains("would close a cycle", exception.Message, StringComparison.Ordinal);

        // Re-adding the same edge is a no-op, not a cycle.
        var same = await graph.AddEdgeAsync(new EdgeDraft(first.Id, second.Id, KnowledgeEdgeTypes.DependsOn));
        Assert.True(same.Id > 0);
    }

    private static InMemoryKnowledgeGraph CreateGraph()
    {
        var catalog = KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [
                new VocabularyPack
                {
                    Name = "acme",
                    NodeKinds = [new NodeKindSpec { Name = Person }],
                    EdgeTypes =
                    [
                        new EdgeTypeSpec
                        {
                            Name = Responds,
                            Pairs = [new EdgeKindPair(KnowledgeKinds.Answer, KnowledgeKinds.Question), new EdgeKindPair(KnowledgeKinds.Answer, KnowledgeKinds.Answer)],
                            InverseName = RespondedBy,
                        },
                        new EdgeTypeSpec
                        {
                            Name = Knows,
                            Pairs = [new EdgeKindPair(Person, Person)],
                            Symmetric = true,
                        },
                    ],
                },
            ]);

        return new InMemoryKnowledgeGraph(
            Options.Create(new KnowledgeOptions()),
            NullLogger<InMemoryKnowledgeGraph>.Instance,
            catalog);
    }
}

/// <summary>Deprecated vocabulary stays writable but is hidden from listings.</summary>
public sealed class DeprecatedVocabularyTests
{
    private const string Legacy = "acme:legacy";
    private const string LegacyLink = "acme:legacy_link";

    [Fact]
    public async Task Deprecated_kind_and_edge_are_hidden_but_writable()
    {
        var catalog = KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [
                new VocabularyPack
                {
                    Name = "acme",
                    NodeKinds = [new NodeKindSpec { Name = Legacy, Deprecated = true }],
                    EdgeTypes =
                    [
                        new EdgeTypeSpec
                        {
                            Name = LegacyLink,
                            Pairs = [new EdgeKindPair(Legacy, KnowledgeKinds.Note)],
                            Deprecated = true,
                        },
                    ],
                },
            ]);

        var graph = new InMemoryKnowledgeGraph(
            Options.Create(new KnowledgeOptions()),
            NullLogger<InMemoryKnowledgeGraph>.Instance,
            catalog);

        Assert.DoesNotContain(Legacy, (await graph.GetKindsAsync()).Select(kind => kind.Name));
        Assert.DoesNotContain(LegacyLink, (await graph.GetEdgeTypesAsync()).Select(edge => edge.Name));

        // Writes still succeed so existing automation can finish the migration.
        var legacy = await graph.CreateNodeAsync(new NodeDraft(Legacy, "old row"));
        var note = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Note, "note"));
        var edge = await graph.AddEdgeAsync(new EdgeDraft(legacy.Id, note.Id, LegacyLink));
        Assert.Equal(LegacyLink, edge.EdgeType);
    }
}

/// <summary>Required properties declared by a pack are enforced on create and patch.</summary>
public sealed class RequiredPropertyTests
{
    private const string Invoice = "acme:invoice";

    [Fact]
    public async Task Missing_required_property_is_rejected_on_create_and_patch()
    {
        var graph = CreateGraph();

        var missing = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.CreateNodeAsync(new NodeDraft(Invoice, "INV-1")));
        Assert.Contains("requires properties repo", missing.Message, StringComparison.Ordinal);

        var created = await graph.CreateNodeAsync(new NodeDraft(
            Invoice,
            "INV-1",
            Properties: new Dictionary<string, string> { ["Repo"] = "anima", ["amount"] = "10" }));

        // A patch replaces the property set, so dropping a required key is rejected.
        var dropped = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.UpdateNodeAsync(created.Id, new NodePatch(
                Properties: new Dictionary<string, string> { ["amount"] = "20" })));
        Assert.Contains("requires properties repo", dropped.Message, StringComparison.Ordinal);

        var blank = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.UpdateNodeAsync(created.Id, new NodePatch(
                Properties: new Dictionary<string, string> { ["repo"] = "", ["amount"] = "20" })));
        Assert.Contains("requires properties repo", blank.Message, StringComparison.Ordinal);

        var updated = await graph.UpdateNodeAsync(created.Id, new NodePatch(
            Properties: new Dictionary<string, string> { ["repo"] = "other", ["amount"] = "20" }));
        Assert.Equal("other", updated.Properties["repo"]);
    }

    [Fact]
    public async Task Core_kinds_without_required_props_are_unaffected()
    {
        var graph = CreateGraph();
        var note = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Note, "free form"));
        Assert.Equal(KnowledgeKinds.Note, note.Kind);
    }

    private static InMemoryKnowledgeGraph CreateGraph()
    {
        var catalog = KnowledgeVocabularyLoader.Build(
            KnowledgeVocabularyLoader.LoadEmbeddedCore(),
            [
                new VocabularyPack
                {
                    Name = "acme",
                    NodeKinds = [new NodeKindSpec { Name = Invoice, RequiredProps = ["repo"] }],
                },
            ]);

        return new InMemoryKnowledgeGraph(
            Options.Create(new KnowledgeOptions()),
            NullLogger<InMemoryKnowledgeGraph>.Instance,
            catalog);
    }
}
