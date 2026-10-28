using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge;

/// <summary>
/// Publishes compact <c>anima.knowledge.v1.GraphEvent</c> notifications after
/// successful graph mutations. Events are wake-ups for agents/the UI; readers
/// still fetch state from the HTTP API. Publish failures never fail the write.
/// </summary>
public sealed class PublishingKnowledgeGraph : IKnowledgeGraph, IAsyncDisposable
{
    private readonly IKnowledgeGraph _inner;
    private readonly IBusPublisher? _bus;
    private readonly KnowledgeOptions _options;
    private readonly ILogger<PublishingKnowledgeGraph> _logger;
    private readonly Channel<MessageEnvelope>? _queue;
    private readonly Task? _pump;

    public PublishingKnowledgeGraph(
        IKnowledgeGraph inner,
        IBusPublisher? bus,
        IOptions<KnowledgeOptions> options,
        ILogger<PublishingKnowledgeGraph> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _bus = bus;
        _options = options.Value;
        _logger = logger;

        if (bus is not null)
        {
            _queue = Channel.CreateBounded<MessageEnvelope>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _pump = Task.Run(PumpAsync);
        }
    }

    public async Task<KnowledgeNode> CreateNodeAsync(NodeDraft draft, CancellationToken cancellationToken = default)
    {
        var node = await _inner.CreateNodeAsync(draft, cancellationToken);
        await PublishAsync("node.created", node, null, cancellationToken);
        return node;
    }

    public async Task<KnowledgeNode> UpdateNodeAsync(long id, NodePatch patch, CancellationToken cancellationToken = default)
    {
        var node = await _inner.UpdateNodeAsync(id, patch, cancellationToken);
        await PublishAsync("node.updated", node, null, cancellationToken);
        return node;
    }

    public async Task<bool> DeleteNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        var node = await _inner.GetNodeAsync(id, cancellationToken);
        bool deleted = await _inner.DeleteNodeAsync(id, cancellationToken);
        if (deleted)
        {
            await PublishAsync("node.deleted", node, null, cancellationToken);
        }

        return deleted;
    }

    public async Task<KnowledgeNode?> GetNodeAsync(long id, CancellationToken cancellationToken = default)
        => await _inner.GetNodeAsync(id, cancellationToken);

    public async Task<NodePage> QueryNodesAsync(NodeQuery query, CancellationToken cancellationToken = default)
        => await _inner.QueryNodesAsync(query, cancellationToken);

    public async Task<NodePage> SearchAsync(string text, NodeQuery? query = null, CancellationToken cancellationToken = default)
        => await _inner.SearchAsync(text, query, cancellationToken);

    public async Task<KnowledgeEdge> AddEdgeAsync(EdgeDraft draft, CancellationToken cancellationToken = default)
    {
        var edge = await _inner.AddEdgeAsync(draft, cancellationToken);
        await PublishAsync("edge.created", null, edge, cancellationToken);
        return edge;
    }

    public async Task<bool> RemoveEdgeAsync(long source, long destination, string edgeType, CancellationToken cancellationToken = default)
    {
        bool removed = await _inner.RemoveEdgeAsync(source, destination, edgeType, cancellationToken);
        if (removed)
        {
            await PublishAsync(
                "edge.removed",
                null,
                new KnowledgeEdge { Id = 0, Source = source, Destination = destination, EdgeType = edgeType },
                cancellationToken);
        }

        return removed;
    }

    public async Task<IReadOnlyList<NeighborResult>> GetNeighborsAsync(NeighborQuery query, CancellationToken cancellationToken = default)
        => await _inner.GetNeighborsAsync(query, cancellationToken);

    public async Task<IReadOnlyList<NodeKindDefinition>> GetKindsAsync(CancellationToken cancellationToken = default)
        => await _inner.GetKindsAsync(cancellationToken);

    public async Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(CancellationToken cancellationToken = default)
        => await _inner.GetEdgeTypesAsync(cancellationToken);

    public async Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        => await _inner.GetHealthAsync(cancellationToken);

    public async Task<GraphSnapshot> GetGraphAsync(GraphSnapshotQuery query, CancellationToken cancellationToken = default)
        => await _inner.GetGraphAsync(query, cancellationToken);

    public async Task<PlanResult> CreatePlanAsync(PlanDraft plan, CancellationToken cancellationToken = default)
    {
        var result = await _inner.CreatePlanAsync(plan, cancellationToken);
        await PublishAsync("plan.created", result.Goal, null, cancellationToken);
        return result;
    }

    public async Task<KnowledgeNode> CreateTaskAsync(TaskDraft draft, CancellationToken cancellationToken = default)
    {
        var task = await _inner.CreateTaskAsync(draft, cancellationToken);
        await PublishAsync("node.created", task, null, cancellationToken);
        return task;
    }

    public async Task<KnowledgeNode> PatchTaskAsync(long id, TaskPatch patch, CancellationToken cancellationToken = default)
    {
        var task = await _inner.PatchTaskAsync(id, patch, cancellationToken);
        await PublishAsync("node.updated", task, null, cancellationToken);
        return task;
    }

    private Task PublishAsync(
        string change,
        KnowledgeNode? node,
        KnowledgeEdge? edge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.PublishEvents || _queue is null)
        {
            return Task.CompletedTask;
        }

        var payload = new JsonObject
        {
            ["change"] = change,
            ["nodeKind"] = node?.Kind ?? string.Empty,
            ["nodeId"] = (node?.Id ?? 0).ToString(CultureInfo.InvariantCulture),
            ["nodeName"] = node?.Name ?? string.Empty,
            ["edgeId"] = (edge?.Id ?? 0).ToString(CultureInfo.InvariantCulture),
            ["source"] = (edge?.Source ?? 0).ToString(CultureInfo.InvariantCulture),
            ["destination"] = (edge?.Destination ?? 0).ToString(CultureInfo.InvariantCulture),
            ["edgeType"] = edge?.EdgeType ?? string.Empty,
            ["timestamp"] = DateTimeOffset.UtcNow,
        };

        var envelope = MessageEnvelope.Create(
            BusTopics.KnowledgeChanged,
            "anima.knowledge.v1.GraphEvent",
            Encoding.UTF8.GetBytes(payload.ToJsonString()),
            ContractContentTypes.ProtobufJson,
            sender: "anima-knowledge");

        if (!_queue.Writer.TryWrite(envelope))
        {
            _logger.LogWarning("Knowledge event queue full; dropped {Change}.", change);
        }

        return Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        if (_queue is null || _bus is null)
        {
            return;
        }

        await foreach (var envelope in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _bus.PublishAsync(envelope, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish knowledge event {Topic}.", envelope.Topic);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_queue is null)
        {
            return;
        }

        _queue.Writer.TryComplete();
        if (_pump is not null)
        {
            await _pump;
        }
    }
}
