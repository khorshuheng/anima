using System.Text;
using System.Text.Json;
using Anima.Automation;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Agent;

public sealed class AgentRuntime : IAgentRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentPlanner _planner;
    private readonly IToolRegistry _tools;
    private readonly IMessageBus _bus;
    private readonly IAgentMailboxFactory _mailboxes;
    private readonly IServiceProvider _services;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentRuntime> _logger;

    public AgentRuntime(
        IAgentPlanner planner,
        IToolRegistry tools,
        IMessageBus bus,
        IAgentMailboxFactory mailboxes,
        IServiceProvider services,
        IOptions<AgentOptions> options,
        ILogger<AgentRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(mailboxes);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _planner = planner;
        _tools = tools;
        _bus = bus;
        _mailboxes = mailboxes;
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        AgentDescriptor agent,
        string input,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var startedAt = DateTimeOffset.UtcNow;
        var turnId = Guid.NewGuid().ToString("n");
        var mergedVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (variables is not null)
        {
            foreach (var pair in variables)
            {
                mergedVariables[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.AutomationBaseUrl))
        {
            mergedVariables.TryAdd("automationBaseUrl", _options.AutomationBaseUrl);
        }

        var context = new AgentContext(agent.Id, _services, mergedVariables);
        var plan = await _planner.PlanAsync(context, input, cancellationToken);

        await PublishAsync(
            agent,
            BusTopics.AgentOutbox(agent.Id),
            "anima.agent.v1.AgentTurnResult",
            new { turnId, agentId = agent.Id, kind = plan.Kind.ToString(), plan.Summary },
            cancellationToken);

        AgentTurnResult result;
        try
        {
            result = plan.Kind switch
            {
                AgentActionKind.ToolCall => await RunToolAsync(agent, turnId, plan, startedAt, mergedVariables, cancellationToken),
                AgentActionKind.Delegate => await DelegateAsync(agent, turnId, plan, startedAt, mergedVariables, cancellationToken),
                AgentActionKind.RunScript => await RunScriptAsync(agent, turnId, plan, startedAt, mergedVariables, cancellationToken),
                AgentActionKind.Escalate => new AgentTurnResult(
                    turnId,
                    agent.Id,
                    AgentActionKind.Escalate,
                    plan.Message ?? "escalated",
                    StartedAt: startedAt,
                    CompletedAt: DateTimeOffset.UtcNow),
                _ => new AgentTurnResult(
                    turnId,
                    agent.Id,
                    AgentActionKind.Respond,
                    plan.Summary ?? plan.Message ?? string.Empty,
                    Output: plan.Message,
                    StartedAt: startedAt,
                    CompletedAt: DateTimeOffset.UtcNow),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Agent turn {TurnId} failed.", turnId);
            result = new AgentTurnResult(
                turnId,
                agent.Id,
                plan.Kind,
                $"failed: {exception.Message}",
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.UtcNow);
        }

        await PublishAsync(
            agent,
            BusTopics.TaskResult,
            "anima.agent.v1.AgentTurnResult",
            result,
            cancellationToken);

        return result;
    }

    private async Task<AgentTurnResult> RunToolAsync(
        AgentDescriptor agent,
        string turnId,
        AgentPlan plan,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        var tool = _tools.Find(plan.ToolName ?? string.Empty)
            ?? throw new InvalidOperationException($"tool '{plan.ToolName}' is not registered");

        var context = new AgentContext(agent.Id, _services, variables);
        var output = await tool.InvokeAsync(plan.Arguments ?? "{}", context, cancellationToken);
        return new AgentTurnResult(
            turnId,
            agent.Id,
            AgentActionKind.ToolCall,
            plan.Summary ?? $"called {tool.Name}",
            ToolName: tool.Name,
            Output: output,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow);
    }

    private async Task<AgentTurnResult> DelegateAsync(
        AgentDescriptor agent,
        string turnId,
        AgentPlan plan,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.Recipient))
        {
            throw new InvalidOperationException("delegate requires a recipient");
        }

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                From = agent.Id,
                plan.Recipient,
                TaskId = turnId,
                Instructions = plan.Message ?? string.Empty,
                Capabilities = agent.Capabilities,
                Variables = variables,
            },
            JsonOptions));

        await using var mailbox = await _mailboxes.CreateAsync(agent.Id, cancellationToken);
        await mailbox.SendAsync(
            plan.Recipient,
            MessageEnvelope.Create(
                BusTopics.AgentInbox(plan.Recipient),
                "anima.agent.v1.Delegation",
                payload,
                ContractContentTypes.Json,
                sender: agent.Id,
                recipient: plan.Recipient,
                correlationId: turnId),
            cancellationToken);

        return new AgentTurnResult(
            turnId,
            agent.Id,
            AgentActionKind.Delegate,
            plan.Summary ?? $"delegated to {plan.Recipient}",
            Recipient: plan.Recipient,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow);
    }

    private async Task<AgentTurnResult> RunScriptAsync(
        AgentDescriptor agent,
        string turnId,
        AgentPlan plan,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.Script))
        {
            throw new InvalidOperationException("script action requires a script body");
        }

        var driver = _services.GetRequiredService<IBrowserDriver>();
        var runner = _services.GetRequiredService<IScriptRunner>();
        var baseUrl = variables.GetValueOrDefault("automationBaseUrl");

        await using var session = await driver.StartSessionAsync(
            new SessionOptions { BaseUrl = baseUrl },
            cancellationToken);

        var result = await runner.RunAsync(
            plan.Script,
            session,
            new ScriptRunOptions
            {
                Variables = new Dictionary<string, string>(variables, StringComparer.Ordinal),
                BaseUrl = baseUrl,
            },
            cancellationToken);

        return new AgentTurnResult(
            turnId,
            agent.Id,
            AgentActionKind.RunScript,
            result.Success ? "script succeeded" : $"script failed: {result.Error}",
            Output: JsonSerializer.Serialize(result, JsonOptions),
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow);
    }

    private async Task PublishAsync(
        AgentDescriptor agent,
        string topic,
        string typeName,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await _bus.PublishAsync(
            MessageEnvelope.Create(topic, typeName, bytes, ContractContentTypes.Json, sender: agent.Id),
            cancellationToken);
    }
}
