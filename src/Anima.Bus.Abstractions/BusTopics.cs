namespace Anima.Bus;

public static class BusTopics
{
    public const string Root = "anima";
    public const string All = "anima.**";
    public const string DeadLetter = "anima.dlq";
    public const string TaskAssigned = "anima.task.assigned";
    public const string TaskResult = "anima.task.result";
    public const string KnowledgeChanged = "anima.knowledge.changed";
    public const string AutomationRun = "anima.automation.run";
    public const string DeployRequested = "anima.deploy.requested";
    public const string DeployFinished = "anima.deploy.finished";

    public static string AgentInbox(string agentId) => $"anima.agent.{Normalize(agentId)}.inbox";

    public static string AgentOutbox(string agentId) => $"anima.agent.{Normalize(agentId)}.outbox";

    private static string Normalize(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return agentId.Trim().ToLowerInvariant();
    }
}
