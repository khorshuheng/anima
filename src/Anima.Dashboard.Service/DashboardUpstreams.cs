namespace Anima.Dashboard.Service;

/// <summary>Named <see cref="HttpClient"/> registrations used by the dashboard BFF.</summary>
public static class DashboardUpstreams
{
    public const string Workflow = "workflow";
    public const string Knowledge = "knowledge";
    public const string Bus = "bus";

    /// <summary>Bus client without a request timeout: SSE connections stay open until the browser leaves.</summary>
    public const string BusStream = "bus-stream";
}
