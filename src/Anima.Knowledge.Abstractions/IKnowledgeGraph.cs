namespace Anima.Knowledge;

/// <summary>
/// Task management and long-term memory over one node/edge graph. The in-memory
/// implementation is the default; <c>Anima.Knowledge.Client</c> implements the
/// same interface against a remote memgraph-compatible HTTP service.
/// </summary>
public interface IKnowledgeGraph
{
    Task<KnowledgeNode> CreateNodeAsync(NodeDraft draft, CancellationToken cancellationToken = default);

    Task<KnowledgeNode?> GetNodeAsync(long id, CancellationToken cancellationToken = default);

    Task<NodePage> QueryNodesAsync(NodeQuery query, CancellationToken cancellationToken = default);

    Task<NodePage> SearchAsync(
        string text,
        NodeQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteNodeAsync(long id, CancellationToken cancellationToken = default);

    Task<KnowledgeEdge> AddEdgeAsync(EdgeDraft draft, CancellationToken cancellationToken = default);

    Task<bool> RemoveEdgeAsync(
        long source,
        long destination,
        string edgeType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NeighborResult>> GetNeighborsAsync(
        NeighborQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NodeKindDefinition>> GetKindsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(CancellationToken cancellationToken = default);

    Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<PlanResult> CreatePlanAsync(PlanDraft plan, CancellationToken cancellationToken = default);

    Task<KnowledgeNode> CreateTaskAsync(TaskDraft draft, CancellationToken cancellationToken = default);

    Task<KnowledgeNode> PatchTaskAsync(
        long id,
        TaskPatch patch,
        CancellationToken cancellationToken = default);
}
