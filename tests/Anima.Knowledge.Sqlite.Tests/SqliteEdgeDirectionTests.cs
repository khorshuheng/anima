using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Sqlite.Tests;

/// <summary>
/// Direction semantics against the SQLite store: one stored edge per fact, an
/// inverse name readable from the other end, and a symmetric relation readable
/// from both ends.
/// </summary>
public sealed class SqliteEdgeDirectionTests : IDisposable
{
    private const string Invoice = "acme:invoice";
    private const string BilledTo = "acme:billed_to";
    private const string BilledFrom = "acme:billed_from";
    private const string Person = "acme:person";
    private const string Knows = "acme:knows";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "anima-direction-" + Guid.NewGuid().ToString("n"));

    public SqliteEdgeDirectionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Inverse_name_reads_the_stored_edge_from_the_other_end()
    {
        using var graph = CreateGraph();
        var invoice = await graph.CreateNodeAsync(new NodeDraft(Invoice, "INV-1"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));
        var stored = await graph.AddEdgeAsync(new EdgeDraft(invoice.Id, repo.Id, BilledTo));

        var reversed = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(repo.Id, EdgeDirection.Outgoing, BilledFrom)));
        Assert.Equal(invoice.Id, reversed.Node.Id);
        Assert.Equal(invoice.Id, reversed.Edge.Source);
        Assert.Equal(stored.Id, reversed.Edge.Id);
        Assert.Equal(EdgeDirection.Incoming, reversed.Direction);

        var canonical = Assert.Single(await graph.GetNeighborsAsync(
            new NeighborQuery(invoice.Id, EdgeDirection.Outgoing, BilledTo)));
        Assert.Equal(repo.Id, canonical.Node.Id);
        Assert.Equal(EdgeDirection.Outgoing, canonical.Direction);
    }

    [Fact]
    public async Task Inverse_name_on_a_write_is_rejected_with_guidance()
    {
        using var graph = CreateGraph();
        var invoice = await graph.CreateNodeAsync(new NodeDraft(Invoice, "INV-1"));
        var repo = await graph.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Repo, "anima"));

        var exception = await Assert.ThrowsAsync<KnowledgeValidationException>(
            () => graph.AddEdgeAsync(new EdgeDraft(repo.Id, invoice.Id, BilledFrom)));

        Assert.Contains("is the inverse of 'acme:billed_to'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symmetric_edge_is_readable_from_both_ends()
    {
        using var graph = CreateGraph();
        var alice = await graph.CreateNodeAsync(new NodeDraft(Person, "alice"));
        var bob = await graph.CreateNodeAsync(new NodeDraft(Person, "bob"));
        await graph.AddEdgeAsync(new EdgeDraft(alice.Id, bob.Id, Knows));

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
        using var graph = CreateGraph();
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

    private SqliteKnowledgeGraph CreateGraph() => new(
        Options.Create(new SqliteKnowledgeOptions
        {
            DatabasePath = Path.Combine(_directory, "knowledge.db"),
            BusyTimeoutSeconds = 5,
        }),
        Options.Create(new KnowledgeOptions()),
        NullLogger<SqliteKnowledgeGraph>.Instance,
        KnowledgeVocabularyLoader.Build(
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
            ]));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
