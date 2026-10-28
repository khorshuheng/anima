namespace Anima.Automation;

/// <summary>
/// Redacts credential-bearing query parameters before a URL enters the event log.
/// Header redaction alone is not enough: tokens are routinely carried in query
/// strings, and the log is streamed to dashboards and kept in browsers.
/// </summary>
public static class BrowserUrlSanitizer
{
    private const string Redacted = "redacted";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token",
        "refresh_token",
        "id_token",
        "api_key",
        "apikey",
        "key",
        "secret",
        "client_secret",
        "password",
        "passwd",
        "pwd",
        "sig",
        "signature",
        "auth",
        "authorization",
        "session",
        "sessionid",
        "jwt",
        "code",
    };

    public static string? Sanitize(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        int queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return url;
        }

        int fragmentStart = url.IndexOf('#', queryStart);
        int queryEnd = fragmentStart < 0 ? url.Length : fragmentStart;
        string query = url[(queryStart + 1)..queryEnd];

        if (query.Length == 0)
        {
            return url;
        }

        string[] parameters = query.Split('&');
        bool changed = false;

        for (int index = 0; index < parameters.Length; index++)
        {
            string parameter = parameters[index];
            if (parameter.Length == 0)
            {
                continue;
            }

            int separator = parameter.IndexOf('=', StringComparison.Ordinal);
            string name = separator < 0 ? parameter : parameter[..separator];

            if (!SensitiveKeys.Contains(Uri.UnescapeDataString(name)))
            {
                continue;
            }

            parameters[index] = $"{name}={Redacted}";
            changed = true;
        }

        if (!changed)
        {
            return url;
        }

        string suffix = fragmentStart < 0 ? string.Empty : url[fragmentStart..];
        return $"{url[..queryStart]}?{string.Join('&', parameters)}{suffix}";
    }
}
