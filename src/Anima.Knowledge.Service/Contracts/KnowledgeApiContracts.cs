namespace Anima.Knowledge.Service.Contracts;

public sealed record CreateNodeRequest(
    string? Kind,
    long? KindId,
    string Name,
    string? Description,
    string? Content,
    Dictionary<string, string>? Properties);

public sealed record CreateEdgeRequest(
    long Source,
    long Destination,
    string? EdgeType,
    long? EdgeTypeId);

public sealed record CreateTaskRequest(
    string Name,
    string? Description,
    string? Content,
    long? Parent,
    List<long>? DependsOn,
    List<long>? Affects);

public sealed record PatchTaskRequest(string? Name, string? Description, string? Content);

public sealed record PlanGoalRequest(string Name, string? Description, string? Content);

public sealed record PlanAnchorRequest(long NodeId, string EdgeType);

public sealed record PlanTaskRequest(
    string Key,
    string Name,
    string? Description,
    string? Content,
    string? Parent,
    List<string>? DependsOn,
    List<long>? Affects,
    Dictionary<string, string>? Properties = null);

public sealed record PlanRequest(
    PlanGoalRequest? Goal,
    long? GoalId,
    PlanAnchorRequest? Anchor,
    List<PlanTaskRequest>? Tasks);
