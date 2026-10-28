namespace Anima.Dashboard.Service;

/// <summary>
/// Maps dashboard BFF routes onto upstream service paths. Paths carry no leading
/// slash so they resolve against the configured <see cref="HttpClient.BaseAddress"/>.
/// </summary>
public static class DashboardRoutes
{
    /// <summary><c>/api/dashboard/workflows[/{id}[/...]]</c> -> workflow <c>api/workflows[/{id}[/...]]</c>.</summary>
    public static string Workflows(string? remainder, string? queryString = null)
    {
        string trimmed = (remainder ?? string.Empty).Trim('/');
        string path = trimmed.Length == 0 ? "api/workflows" : $"api/workflows/{trimmed}";
        return path + queryString;
    }

    /// <summary>
    /// <c>/api/dashboard/graph</c> -> knowledge <c>api/graph</c>, forwarding only the graph
    /// window parameters (<c>newest</c> selects the newest ids first, <c>sinceId</c> filters).
    /// </summary>
    public static string Graph(int? maxNodes = null, int? maxEdges = null, bool? newest = null, long? sinceId = null)
    {
        var parameters = new List<string>(4);
        if (maxNodes is { } nodes)
        {
            parameters.Add($"maxNodes={nodes}");
        }

        if (maxEdges is { } edges)
        {
            parameters.Add($"maxEdges={edges}");
        }

        if (newest is { } newestWindow)
        {
            parameters.Add($"newest={(newestWindow ? "true" : "false")}");
        }

        if (sinceId is { } since)
        {
            parameters.Add($"sinceId={since}");
        }

        return parameters.Count == 0 ? "api/graph" : $"api/graph?{string.Join('&', parameters)}";
    }

    /// <summary><c>/api/dashboard/nodes</c> -> knowledge <c>api/nodes</c> with the caller's filters.</summary>
    public static string Nodes(string? queryString = null) => "api/nodes" + queryString;

    /// <summary><c>/api/dashboard/search</c> -> knowledge <c>api/search</c> with the caller's filters.</summary>
    public static string Search(string? queryString = null) => "api/search" + queryString;

    /// <summary><c>/api/dashboard/kinds</c> -> knowledge <c>api/node-kinds</c>.</summary>
    public const string Kinds = "api/node-kinds";

    /// <summary><c>/api/dashboard/edge-types</c> -> knowledge <c>api/edge-types</c>.</summary>
    public const string EdgeTypes = "api/edge-types";

    /// <summary>
    /// <c>/api/dashboard/vocabulary</c> -> knowledge <c>api/vocabulary</c>: the merged
    /// core pack plus any loaded user packs.
    /// </summary>
    public const string Vocabulary = "api/vocabulary";

    /// <summary><c>/api/dashboard/bus</c> -> bus <c>api/bus/snapshot</c>.</summary>
    public const string BusSnapshot = "api/bus/snapshot";

    /// <summary><c>/api/dashboard/bus/stream</c> -> bus <c>api/bus/stream</c> with the caller's topics filter.</summary>
    public static string BusStream(string? queryString = null) => "api/bus/stream" + queryString;
}
