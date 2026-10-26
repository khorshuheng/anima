namespace Anima.Automation;

/// <summary>What a captured session event describes.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BrowserEventKind>))]
public enum BrowserEventKind
{
    /// <summary><c>console.*</c> output from the page.</summary>
    Console,

    /// <summary>An unhandled exception in page script.</summary>
    PageError,

    /// <summary>A network request was issued.</summary>
    Request,

    /// <summary>A network response arrived.</summary>
    Response,

    /// <summary>A request failed before a response was received.</summary>
    RequestFailed,

    /// <summary>The main frame committed a navigation.</summary>
    Navigation,

    /// <summary>A JavaScript dialog opened and was answered.</summary>
    Dialog,
}

/// <summary>How the driver answers a dialog no script step handled.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BrowserDialogBehavior>))]
public enum BrowserDialogBehavior
{
    /// <summary>Dismiss, which is what Playwright does when no handler is attached.</summary>
    Dismiss,

    /// <summary>Accept, for scripts that must click through a <c>confirm()</c>.</summary>
    Accept,
}

/// <summary>
/// One captured page or network event. Metadata is always present; headers and
/// bodies are only populated when the session opted in.
/// </summary>
public sealed record BrowserEvent
{
    /// <summary>Monotonic per session, starting at 1; used as the poll cursor.</summary>
    public long Sequence { get; init; }

    public required BrowserEventKind Kind { get; init; }

    /// <summary>UTC timestamp assigned when the event is recorded.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Console level or severity, such as <c>warning</c> or <c>error</c>.</summary>
    public string? Level { get; init; }

    public string? Url { get; init; }

    public string? Method { get; init; }

    public int? Status { get; init; }

    /// <summary>Request resource type: document, script, xhr, image, ...</summary>
    public string? ResourceType { get; init; }

    /// <summary>
    /// Correlates the Request, Response and RequestFailed events of one exchange,
    /// so a caller can tell two writes to the same URL apart.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>Console text, error message or dialog message.</summary>
    public string? Text { get; init; }

    /// <summary>Extra discriminator: dialog type, failure reason, navigation phase.</summary>
    public string? Detail { get; init; }

    /// <summary>Request-to-response latency in milliseconds when known.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Present only when the session requested header capture; secrets redacted.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Present only when the session requested body capture; truncated.</summary>
    public string? Body { get; init; }
}
