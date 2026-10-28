using System.Net;
using System.Text;

namespace Anima.Knowledge.Client.Tests;

/// <summary>
/// The client is how the workflow service reaches knowledge, so a wire-shape drift here
/// silently empties traits, pairs and inverse names with every unit test still green.
/// These tests pin the mapping against canned service responses.
/// </summary>
public sealed class VocabularyWireTests
{
    private const string KindsJson = """
        {
          "items": [
            {
              "id": 2, "name": "task", "description": "A unit of work in a plan tree.",
              "required_props": [], "traits": ["plannable"], "source": "core", "deprecated": false
            },
            {
              "id": 49032, "name": "acme:invoice", "description": "A billable unit.",
              "required_props": ["repo", "amount"], "traits": [], "source": "acme", "deprecated": true
            }
          ]
        }
        """;

    private const string EdgeTypesJson = """
        {
          "items": [
            {
              "id": 1, "name": "part_of", "description": "Decomposition.",
              "resolves": false, "single_outgoing": false, "symmetric": false, "acyclic": true,
              "inverse_name": null,
              "source_kinds": ["task"], "destination_kinds": ["goal", "task"],
              "pairs": [["task", "goal"], ["task", "task"]],
              "traits": ["hierarchy"], "source": "core", "deprecated": false
            },
            {
              "id": 45090, "name": "acme:billed_to", "description": "",
              "resolves": false, "single_outgoing": false, "symmetric": false, "acyclic": false,
              "inverse_name": "acme:billed_from",
              "source_kinds": ["acme:invoice"], "destination_kinds": ["repo"],
              "pairs": [["acme:invoice", "repo"]],
              "traits": [], "source": "acme", "deprecated": false
            }
          ]
        }
        """;

    [Fact]
    public async Task Kinds_carry_traits_required_props_source_and_deprecation()
    {
        var graph = CreateGraph(new Dictionary<string, string>
        {
            ["api/node-kinds"] = KindsJson,
            ["api/edge-types"] = EdgeTypesJson,
        });

        var kinds = await graph.GetKindsAsync();

        var task = Assert.Single(kinds, kind => kind.Name == "task");
        Assert.Equal([KnowledgeTraits.Plannable], [.. task.Traits]);
        Assert.Equal(KnowledgeVocabularyCatalog.CorePack, task.Source);

        var invoice = Assert.Single(kinds, kind => kind.Name == "acme:invoice");
        Assert.Equal(new[] { "repo", "amount" }, invoice.RequiredProps.ToArray());
        Assert.Equal("acme", invoice.Source);
        Assert.True(invoice.Deprecated);
    }

    [Fact]
    public async Task Edge_types_carry_pairs_inverse_and_acyclic()
    {
        var graph = CreateGraph(new Dictionary<string, string>
        {
            ["api/node-kinds"] = KindsJson,
            ["api/edge-types"] = EdgeTypesJson,
        });

        var edgeTypes = await graph.GetEdgeTypesAsync();

        var partOf = Assert.Single(edgeTypes, edge => edge.Name == KnowledgeEdgeTypes.PartOf);
        Assert.True(partOf.Acyclic);
        Assert.Equal([KnowledgeTraits.Hierarchy], [.. partOf.Traits]);
        Assert.Equal(2, partOf.Pairs.Count);

        var billedTo = Assert.Single(edgeTypes, edge => edge.Name == "acme:billed_to");
        Assert.Equal("acme:billed_from", billedTo.InverseName);
        Assert.Equal([new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)], [.. billedTo.Pairs]);
    }

    [Fact]
    public async Task Neighbor_queries_send_direction_and_accept_inverse_names()
    {
        var handler = new StubHandler(new Dictionary<string, string>
        {
            ["api/node-kinds"] = KindsJson,
            ["api/edge-types"] = EdgeTypesJson,
            ["api/nodes/7/neighbors"] = """{ "items": [] }""",
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://knowledge.local/") };
        var graph = new HttpKnowledgeGraph(http);

        await graph.GetNeighborsAsync(new NeighborQuery(7, EdgeDirection.Outgoing, "acme:billed_from"));

        var request = handler.Requests[^1];
        Assert.Contains("direction=outgoing", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("type=acme%3Abilled_from", request.PathAndQuery, StringComparison.Ordinal);
    }

    private static HttpKnowledgeGraph CreateGraph(Dictionary<string, string> responses)
    {
        var handler = new StubHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://knowledge.local/") };
        return new HttpKnowledgeGraph(http);
    }

    private sealed class StubHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("request has no uri");
            Requests.Add(uri);
            string key = uri.AbsolutePath.TrimStart('/');
            if (!responses.TryGetValue(key, out string? body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        $$"""{"message":"no stub for {{key}}"}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
