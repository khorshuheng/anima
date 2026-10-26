namespace Anima.Automation;

/// <summary>
/// Selects a captured response. Every field is optional; unset fields match
/// anything, so an empty matcher means "the next response of any kind".
/// </summary>
public sealed record ResponseMatcher
{
    /// <summary>HTTP method, compared case-insensitively (for example <c>POST</c>).</summary>
    public string? Method { get; init; }

    /// <summary>
    /// Glob pattern over the full URL (<c>*</c> wildcard). Matched against the
    /// sanitized URL, so a pattern cannot rely on a redacted credential value.
    /// </summary>
    public string? UrlPattern { get; init; }

    /// <summary>Exact HTTP status code.</summary>
    public int? Status { get; init; }

    /// <summary>How long to wait; falls back to the session timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Also return the response body, read on demand when the session did not opt
    /// into capturing every body.
    /// </summary>
    public bool IncludeBody { get; init; }

    public bool Matches(BrowserEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Kind != BrowserEventKind.Response)
        {
            return false;
        }

        if (Method is not null && !string.Equals(item.Method, Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Status is not null && item.Status != Status)
        {
            return false;
        }

        return GlobPattern.IsMatch(item.Url, UrlPattern);
    }

    /// <summary>Human-readable form used in timeout messages.</summary>
    public override string ToString()
    {
        var parts = new List<string>();

        if (Method is not null)
        {
            parts.Add($"method={Method}");
        }

        if (UrlPattern is not null)
        {
            parts.Add($"url={UrlPattern}");
        }

        if (Status is not null)
        {
            parts.Add($"status={Status}");
        }

        if (IncludeBody)
        {
            parts.Add("body");
        }

        return parts.Count == 0 ? "any response" : string.Join(", ", parts);
    }
}
