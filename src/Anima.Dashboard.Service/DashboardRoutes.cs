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
        var trimmed = (remainder ?? string.Empty).Trim('/');
        var path = trimmed.Length == 0 ? "api/workflows" : $"api/workflows/{trimmed}";
        return path + queryString;
    }

    /// <summary><c>/api/dashboard/graph</c> -> knowledge <c>api/graph</c>, forwarding only the graph caps.</summary>
    public static string Graph(int? maxNodes = null, int? maxEdges = null)
    {
        var caps = new List<string>(2);
        if (maxNodes is { } nodes)
        {
            caps.Add($"maxNodes={nodes}");
        }

        if (maxEdges is { } edges)
        {
            caps.Add($"maxEdges={edges}");
        }

        return caps.Count == 0 ? "api/graph" : $"api/graph?{string.Join('&', caps)}";
    }

    /// <summary><c>/api/dashboard/bus</c> -> bus <c>api/bus/snapshot</c>.</summary>
    public static string BusSnapshot() => "api/bus/snapshot";

    /// <summary><c>/api/dashboard/bus/stream</c> -> bus <c>api/bus/stream</c> with the caller's topics filter.</summary>
    public static string BusStream(string? queryString = null) => "api/bus/stream" + queryString;
}
