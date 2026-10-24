using Anima.Hosting;

namespace Anima.Dashboard.Service;

/// <summary>Dashboard upstream addresses and the read timeout applied to proxied GETs.</summary>
public sealed record DashboardOptions
{
    public string WorkflowBaseUrl { get; init; } = $"http://127.0.0.1:{AnimaPorts.Workflow}";

    public string KnowledgeBaseUrl { get; init; } = $"http://127.0.0.1:{AnimaPorts.Knowledge}";

    public string BusBaseUrl { get; init; } = $"http://127.0.0.1:{AnimaPorts.Bus}";

    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
