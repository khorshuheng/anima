namespace Anima.Automation;

public sealed record SessionOptions
{
    /// <summary>Base URL used to resolve relative navigations.</summary>
    public string? BaseUrl { get; init; }

    public bool Headless { get; init; } = true;

    public double TimeoutMs { get; init; } = 30_000;

    public bool IgnoreHttpsErrors { get; init; } = true;

    /// <summary>Optional Playwright trace output path.</summary>
    public string? TracePath { get; init; }

    /// <summary>
    /// Capture request/response headers in the session event log. Secrets
    /// (authorization, cookie, set-cookie) are redacted even when enabled.
    /// </summary>
    public bool CaptureHeaders { get; init; }

    /// <summary>Capture request and response bodies in the session event log, truncated.</summary>
    public bool CaptureBodies { get; init; }

    /// <summary>How a dialog is answered when no script step handled it.</summary>
    public BrowserDialogBehavior DialogBehavior { get; init; } = BrowserDialogBehavior.Dismiss;
}

public enum WaitState
{
    Visible,
    Hidden,
    Attached,
    Detached,
}

/// <summary>Starts browser sessions. Implementations own the browser process.</summary>
public interface IBrowserDriver
{
    string Name { get; }

    Task<IBrowserSession> StartSessionAsync(
        SessionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One scripted browser session. This is the seam the DSL drives; Playwright is
/// an implementation detail, and raw CDP is available when a step needs it.
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    string Id { get; }

    string? CurrentUrl { get; }

    Task NavigateAsync(string url, CancellationToken cancellationToken = default);

    Task ClickAsync(string target, CancellationToken cancellationToken = default);

    Task FillAsync(string target, string value, CancellationToken cancellationToken = default);

    Task PressAsync(string target, string key, CancellationToken cancellationToken = default);

    Task SelectAsync(string target, string value, CancellationToken cancellationToken = default);

    Task WaitForAsync(
        string target,
        WaitState state = WaitState.Visible,
        CancellationToken cancellationToken = default);

    Task<string?> GetTextAsync(string target, CancellationToken cancellationToken = default);

    Task<string?> GetAttributeAsync(
        string target,
        string attribute,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> QueryAllTextAsync(
        string target,
        CancellationToken cancellationToken = default);

    Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default);

    Task<string> TakeScreenshotAsync(string? path = null, CancellationToken cancellationToken = default);

    /// <summary>Raw Chrome DevTools Protocol channel, when the driver supports it.</summary>
    ICdpTransport? Cdp { get; }

    /// <summary>Page/network events captured so far, oldest first.</summary>
    IReadOnlyList<BrowserEvent> ReadEvents(long afterSequence = 0, int limit = 200);

    /// <summary>
    /// Replays buffered events after <paramref name="afterSequence"/>, then streams
    /// live ones for as long as the caller reads.
    /// </summary>
    IAsyncEnumerable<BrowserEvent> SubscribeEventsAsync(
        long afterSequence = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Events evicted because the session buffer was full.</summary>
    long DroppedEventCount { get; }

    /// <summary>
    /// Waits for a network response matching <paramref name="matcher"/> and returns
    /// its event (status, url, headers/body when captured). A response already
    /// recorded before the call counts, which is what makes "click then confirm the
    /// write" work; each response satisfies at most one wait. Throws
    /// <see cref="TimeoutException"/> when nothing matches in time, and
    /// <see cref="InvalidOperationException"/> when the session closes first.
    /// </summary>
    Task<BrowserEvent> WaitForResponseAsync(
        ResponseMatcher matcher,
        CancellationToken cancellationToken = default);
}

public interface ICdpTransport
{
    Task<string> SendAsync(
        string method,
        string? parametersJson = null,
        CancellationToken cancellationToken = default);
}
