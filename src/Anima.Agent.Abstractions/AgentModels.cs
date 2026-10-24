namespace Anima.Agent;

public sealed record AgentDescriptor(
    string Id,
    string Name,
    IReadOnlyList<string> Capabilities,
    string? Description = null);

public enum AgentActionKind
{
    Respond,
    Delegate,
    ToolCall,
    RunScript,
    Escalate,
}

/// <summary>
/// The decision for one turn. A planner is deliberately small and replaceable so
/// an LLM-backed planner can drop in without touching the runtime.
/// </summary>
public sealed record AgentPlan(
    AgentActionKind Kind,
    string? Summary = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Recipient = null,
    string? Message = null,
    string? Script = null);

public sealed record AgentContext(
    string AgentId,
    IServiceProvider Services,
    IReadOnlyDictionary<string, string> Variables);

public sealed record AgentTurnResult(
    string TurnId,
    string AgentId,
    AgentActionKind Kind,
    string Summary,
    string? Recipient = null,
    string? ToolName = null,
    string? Output = null,
    DateTimeOffset StartedAt = default,
    DateTimeOffset CompletedAt = default);

public interface IAgentPlanner
{
    Task<AgentPlan> PlanAsync(
        AgentContext context,
        string input,
        CancellationToken cancellationToken = default);
}

public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    Task<string> InvokeAsync(
        string argumentsJson,
        AgentContext context,
        CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    IReadOnlyCollection<IAgentTool> Tools { get; }

    IAgentTool? Find(string name);
}

public interface IAgentRuntime
{
    Task<AgentTurnResult> RunTurnAsync(
        AgentDescriptor agent,
        string input,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default);
}
