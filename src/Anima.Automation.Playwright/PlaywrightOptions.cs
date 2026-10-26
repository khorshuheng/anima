namespace Anima.Automation.Playwright;

public sealed class PlaywrightOptions
{
    /// <summary>
    /// Browser family: chrome (default), chromium, msedge, firefox or webkit.
    /// Empty falls back to ANIMA_BROWSER. Chrome uses the installed executable.
    /// </summary>
    public string? BrowserName { get; set; } = "chrome";

    /// <summary>
    /// How the driver obtains Chrome: <c>launch</c> (throwaway profile, one
    /// context per session), <c>profile</c> (persistent user data directory
    /// shared by all sessions) or <c>connect</c> (attach to a running Chrome over
    /// CDP). Empty falls back to ANIMA_BROWSER_MODE, then <c>launch</c>.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>Optional channel such as <c>chrome</c> or <c>msedge</c>.</summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Optional explicit browser executable. When empty, Chrome/Chromium/Edge
    /// are discovered from the system (ANIMA_CHROME_PATH wins) so no Playwright
    /// browser download is needed.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Persistent user data directory used by mode <c>profile</c>; cookies and
    /// logins survive restarts. Empty falls back to ANIMA_CHROME_PROFILE, then to
    /// an Anima-owned directory (never the user's real Chrome profile). A leading
    /// <c>~/</c> is expanded. Ignored by the other modes.
    /// </summary>
    public string? UserDataDirectory { get; set; }

    /// <summary>
    /// CDP endpoint used by mode <c>connect</c>, for example
    /// <c>http://127.0.0.1:9222</c>. Empty falls back to ANIMA_CDP_ENDPOINT, then
    /// to <c>http://127.0.0.1:9222</c>. The target Chrome must be started with
    /// <c>--remote-debugging-port</c> and, on Chrome 136+, a non-default
    /// <c>--user-data-dir</c>.
    /// </summary>
    public string? ConnectEndpoint { get; set; }

    /// <summary>Slows down operations; useful when debugging a script.</summary>
    public double SlowMoMs { get; set; }

    /// <summary>
    /// How long a selector-based interaction (click, fill, type, check, select, ...)
    /// waits for its element. Short on purpose: a wrong selector should fail in
    /// seconds, not after the full session timeout, and a slow page should wait with
    /// an explicit waitFor instead. Per-action <c>timeoutMs</c> overrides it.
    /// </summary>
    public double InteractionTimeoutMs { get; set; } = 4_000;

    /// <summary>
    /// Launch Chrome without a window. Off by default: a headed browser is visible,
    /// watchable and matches what the operator sees, at the cost of needing a
    /// display. Set this (or the HOST env var) for CI, headless servers and agent
    /// containers, where Chrome cannot open a window at all.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>
    /// Accept invalid or self-signed certificates for contexts the driver creates
    /// (launch and profile modes). Connect mode attaches to an existing context, so
    /// there the certificate policy belongs to the Chrome that was started; the
    /// driver logs a warning instead of silently ignoring this value.
    /// </summary>
    public bool IgnoreHttpsErrors { get; set; } = true;

    /// <summary>
    /// Directory for captured screenshots. Absolute by default (the temp directory),
    /// because a service's working directory is not a reliable place to write; set
    /// an absolute path to keep artifacts with the repo or a volume. A relative
    /// value resolves against the service's working directory.
    /// </summary>
    public string? ScreenshotDirectory { get; set; }

    /// <summary>
    /// Directory that <c>setinputfiles</c> may read from. When set, uploads outside
    /// it are rejected; when empty, any path the service user can read is allowed,
    /// which is only appropriate because this service is localhost-only.
    /// </summary>
    public string? UploadDirectory { get; set; }

    /// <summary>Browser download directory override used by install scripts.</summary>
    public string? BrowserCacheDirectory { get; set; }

    /// <summary>
    /// Page/network events kept per session before the oldest are evicted. Polling
    /// and the SSE stream both read this buffer, so it bounds request-log memory.
    /// </summary>
    public int EventBufferSize { get; set; } = 500;
}
