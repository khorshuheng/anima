using Microsoft.Extensions.Logging;

namespace Anima.Agent;

/// <summary>
/// Deterministic stand-in planner:
/// <c>/tool.name {json}</c> calls a tool, <c>delegate:agent-id: message</c>
/// delegates, <c>script: &lt;yaml&gt;</c> runs a UI script, anything else responds.
/// Replace with an LLM planner: <see cref="IAgentPlanner"/> is the seam.
/// </summary>
public sealed class DefaultAgentPlanner : IAgentPlanner
{
    private readonly ILogger<DefaultAgentPlanner> _logger;

    public DefaultAgentPlanner(ILogger<DefaultAgentPlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<AgentPlan> PlanAsync(
        AgentContext context,
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Task.FromResult(new AgentPlan(AgentActionKind.Respond, "empty turn"));
        }

        if (trimmed.StartsWith("delegate:", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = trimmed["delegate:".Length..];
            var separator = remainder.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                var recipient = remainder[..separator].Trim();
                var message = remainder[(separator + 1)..].Trim();
                if (recipient.Length > 0)
                {
                    return Task.FromResult(new AgentPlan(
                        AgentActionKind.Delegate,
                        $"delegate to {recipient}",
                        Recipient: recipient,
                        Message: message));
                }
            }

            _logger.LogWarning("Malformed delegate instruction: {Input}", trimmed);
            return Task.FromResult(new AgentPlan(
                AgentActionKind.Respond,
                "malformed delegate instruction; expected delegate:<agent>:<message>"));
        }

        if (trimmed.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new AgentPlan(
                AgentActionKind.RunScript,
                "run derived UI script",
                Script: trimmed["script:".Length..].Trim()));
        }

        if (trimmed.StartsWith('/'))
        {
            var withoutSlash = trimmed[1..];
            var separator = withoutSlash.IndexOf(' ', StringComparison.Ordinal);
            var toolName = separator < 0 ? withoutSlash : withoutSlash[..separator];
            var arguments = separator < 0 ? "{}" : withoutSlash[(separator + 1)..].Trim();

            return Task.FromResult(new AgentPlan(
                AgentActionKind.ToolCall,
                $"call {toolName}",
                ToolName: toolName,
                Arguments: arguments.Length == 0 ? "{}" : arguments));
        }

        return Task.FromResult(new AgentPlan(
            AgentActionKind.Respond,
            trimmed,
            Message: trimmed));
    }
}
