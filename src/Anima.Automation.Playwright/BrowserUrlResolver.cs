namespace Anima.Automation.Playwright;

/// <summary>
/// Resolves a navigation target against a session base URL. Profile and connect
/// modes share one context and therefore cannot carry a per-session Playwright
/// BaseURL, so this is the only resolution step there.
/// </summary>
public static class BrowserUrlResolver
{
    public static string Resolve(string url, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        // Uri.TryCreate reports a rooted path such as "/login" as an absolute
        // file: URI (Unix) or a rooted path URI (Windows), so a file scheme is not
        // an absolute web URL and must still be resolved against the base URL.
        // Callers that already wrote an absolute URL get their string back
        // unchanged: no Uri round-trip means no re-escaping of data: or opaque URLs.
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && !absolute.IsFile)
        {
            return url;
        }

        if (!string.IsNullOrWhiteSpace(baseUrl)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            && !baseUri.IsFile)
        {
            // AbsoluteUri, not ToString: ToString returns the unescaped display
            // form, which would send a literal space to the browser.
            return new Uri(baseUri, url).AbsoluteUri;
        }

        return url;
    }
}
