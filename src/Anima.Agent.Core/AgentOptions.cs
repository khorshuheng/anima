namespace Anima.Agent;

public sealed class AgentOptions
{
    public string Id { get; set; } = "orchestrator";

    public string Name { get; set; } = "Orchestrator";

    public string? Description { get; set; } = "Default Anima agent.";

    public List<string> Capabilities { get; set; } = ["plan", "delegate", "knowledge", "automation"];

    /// <summary>Base URL used when the runtime starts its own automation session.</summary>
    public string? AutomationBaseUrl { get; set; }

    public AgentDescriptor ToDescriptor() => new(
        Id,
        Name,
        Capabilities,
        Description);
}
