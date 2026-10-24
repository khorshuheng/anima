namespace Anima.Knowledge.Client;

public sealed class KnowledgeClientOptions
{
    /// <summary>Base URL of the knowledge service (or an existing memgraph instance).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:7411";

    /// <summary>Session id sent on graph requests, attributed by memgraph to the caller.</summary>
    public string? SessionId { get; set; } = "anima";

    public string SessionHeader { get; set; } = "X-Session-Id";
}
