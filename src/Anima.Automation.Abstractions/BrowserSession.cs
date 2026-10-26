namespace Anima.Automation;

/// <summary>Click modifiers and button, for grids that multi-select on Ctrl-click.</summary>
public sealed record BrowserClickOptions
{
    /// <summary><c>left</c> (default), <c>right</c> or <c>middle</c>.</summary>
    public string? Button { get; init; }

    public int ClickCount { get; init; } = 1;

    /// <summary>Held during the click: <c>Control</c>, <c>Shift</c>, <c>Alt</c>, <c>Meta</c>.</summary>
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>Offset inside the element, for clicks on a grid cell edge or handle.</summary>
    public BrowserPosition? Position { get; init; }
}

/// <summary>
/// One field of a form: free text, a toggle (switch/checkbox/radio), or a set of
/// choices (select, multi-select, antd Select).
/// </summary>
public sealed record FormFieldValue
{
    public string? Text { get; init; }

    public bool? Toggle { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    public static FormFieldValue FromText(string value) => new() { Text = value };

    public static FormFieldValue FromToggle(bool value) => new() { Toggle = value };

    public static FormFieldValue FromChoices(IEnumerable<string> values)
        => new() { Choices = [.. values] };

    public override string ToString()
        => Toggle is { } toggle
            ? (toggle ? "true" : "false")
            : Choices is { } choices
                ? $"[{string.Join(", ", choices)}]"
                : Text ?? "<empty>";
}

/// <summary>
/// Current state of one form control, keyed for diffing against source data:
/// <see cref="Key"/> is the id, falling back to the name or aria-label.
/// </summary>
public sealed record FormFieldState(
    string Key,
    string? Id,
    string? Name,
    string? Label,
    string Control,
    string Tag,
    string? Value,
    bool? Toggle,
    IReadOnlyList<string>? Choices,
    IReadOnlyList<string>? Labels = null);

/// <summary>
/// What <see cref="IBrowserSession.FillFormAsync"/> did for one field. Only returned
/// fields are in the result: a field that could not be applied or verified throws, so
/// there is no per-field success flag to misread.
/// </summary>
public sealed record FormFieldResult(
    string Selector,
    string Control,
    string Requested,
    string? Actual,
    string? Label = null);

/// <summary>Offset in CSS pixels from the element's top-left corner.</summary>
public sealed record BrowserPosition(double X, double Y);

/// <summary>Options for <see cref="IBrowserSession.TypeAsync(string, string, BrowserTypeOptions?, CancellationToken)"/>.</summary>
public sealed record BrowserTypeOptions
{
    /// <summary>Milliseconds between keystrokes; useful for debounced filters.</summary>
    public int? DelayMs { get; init; }

    /// <summary>Empty the field first, so a filter starts from scratch.</summary>
    public bool ClearFirst { get; init; }
}

/// <summary>Page load states that can be awaited.</summary>
public enum BrowserLoadState
{
    Load,
    DomContentLoaded,
    NetworkIdle,
}

/// <summary>Rendered geometry of an element, in CSS pixels.</summary>
public sealed record BrowserBox(double X, double Y, double Width, double Height);

public sealed record BrowserScreenshotOptions
{
    /// <summary>Explicit output file; otherwise derived from <see cref="Name"/> or a timestamp.</summary>
    public string? Path { get; init; }

    /// <summary>Stable file name (no extension), for callers that re-capture the same view.</summary>
    public string? Name { get; init; }

    /// <summary>Capture the whole scrollable page instead of the viewport.</summary>
    public bool FullPage { get; init; }

    /// <summary>Cap the captured height in CSS pixels; null means uncapped.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Capture only this element instead of the page.</summary>
    public string? Target { get; init; }
}

/// <summary>
/// A captured screenshot. The path is the primary handle - a vision-capable agent
/// loads the file - while the dimensions and size let it decide whether to look.
/// </summary>
public sealed record BrowserScreenshot
{
    public required string Path { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Size of the PNG on disk.</summary>
    public required long Bytes { get; init; }

    public bool FullPage { get; init; }

    /// <summary>Scroll height of the page; greater than <see cref="Height"/> when clipped.</summary>
    public int DocumentHeight { get; init; }

    /// <summary>True when the requested height cap clipped the capture.</summary>
    public bool Truncated { get; init; }
}

public sealed record SessionOptions
{
    /// <summary>Base URL used to resolve relative navigations.</summary>
    public string? BaseUrl { get; init; }

    public bool Headless { get; init; }

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

    Task ClickAsync(
        string target,
        BrowserClickOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Double click, for grids and cells whose editor opens on dblclick.</summary>
    Task DoubleClickAsync(
        string target,
        BrowserClickOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Types character by character, firing a keydown/input per character. Needed by
    /// widgets that filter on each keystroke (antd Select, comboboxes); use
    /// <see cref="FillAsync"/> for plain inputs.
    /// </summary>
    Task TypeAsync(
        string target,
        string value,
        BrowserTypeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Empties an input.</summary>
    Task ClearAsync(string target, CancellationToken cancellationToken = default);

    Task HoverAsync(string target, CancellationToken cancellationToken = default);

    /// <summary>Sets a checkbox or radio to a state, asserting the result.</summary>
    Task CheckAsync(string target, bool isChecked = true, CancellationToken cancellationToken = default);

    /// <summary>Wheel scrolling; with a target the wheel is sent over that element.</summary>
    Task ScrollAsync(
        int deltaX,
        int deltaY,
        string? target = null,
        CancellationToken cancellationToken = default);

    Task ScrollIntoViewAsync(string target, CancellationToken cancellationToken = default);

    Task DragToAsync(string source, string target, CancellationToken cancellationToken = default);

    /// <summary>Uploads files from a path on the service host.</summary>
    Task SetInputFilesAsync(string target, string path, CancellationToken cancellationToken = default);

    Task ReloadAsync(CancellationToken cancellationToken = default);

    Task GoBackAsync(CancellationToken cancellationToken = default);

    Task SetViewportAsync(int width, int height, CancellationToken cancellationToken = default);

    Task FillAsync(string target, string value, CancellationToken cancellationToken = default);

    Task PressAsync(string target, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presses a key on the page itself, with no target: Escape to dismiss a modal,
    /// Enter to submit, PageDown to scroll.
    /// </summary>
    Task PressKeyAsync(string key, CancellationToken cancellationToken = default);

    Task SelectAsync(string target, string value, CancellationToken cancellationToken = default);

    /// <summary>Selects several options at once, for a native multiple-select list.</summary>
    Task SelectAsync(
        string target,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default);

    Task WaitForAsync(
        string target,
        WaitState state = WaitState.Visible,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fills several fields by selector, choosing the interaction from the control
    /// itself: text inputs and textareas, native selects (single and multiple),
    /// checkboxes, radios, and the antd equivalents (Select, Switch, Checkbox,
    /// Radio). Each field is verified after it is set; the first failure throws with
    /// the selector, the value and the reason.
    /// </summary>
    Task<IReadOnlyList<FormFieldResult>> FillFormAsync(
        IReadOnlyDictionary<string, FormFieldValue> fields,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every form control under <paramref name="root"/> (default: the page) with
    /// its current value, so a filled form can be diffed against the expected data in
    /// one call. Controls are classified the same way <see cref="FillFormAsync"/> does.
    /// </summary>
    Task<IReadOnlyList<FormFieldState>> GetFormStateAsync(
        string? root = null,
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

    Task<string?> GetValueAsync(string target, CancellationToken cancellationToken = default);

    Task<int> CountAsync(string target, CancellationToken cancellationToken = default);

    Task<string?> GetHtmlAsync(string target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accessibility snapshot as text: roles, names and structure instead of pixels.
    /// Usually a better first look than a screenshot for a vision-capable agent.
    /// </summary>
    Task<string> AriaSnapshotAsync(string? target = null, CancellationToken cancellationToken = default);

    Task<bool> IsVisibleAsync(string target, CancellationToken cancellationToken = default);

    Task<bool> IsEnabledAsync(string target, CancellationToken cancellationToken = default);

    Task<bool> IsCheckedAsync(string target, CancellationToken cancellationToken = default);

    Task<BrowserBox?> GetBoxAsync(string target, CancellationToken cancellationToken = default);

    /// <summary>Waits until the page URL matches a glob.</summary>
    Task WaitForUrlAsync(
        string urlPattern,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>Waits until a selector matches exactly <paramref name="count"/> elements.</summary>
    Task WaitForCountAsync(
        string target,
        int count,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    Task WaitForLoadStateAsync(
        BrowserLoadState state = BrowserLoadState.Load,
        CancellationToken cancellationToken = default);

    Task<BrowserScreenshot> TakeScreenshotAsync(
        BrowserScreenshotOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Base timeout in milliseconds: the session default, used by waits and, when no
    /// override is set, by interactions.
    /// </summary>
    double DefaultTimeoutMs { get; set; }

    /// <summary>
    /// Per-action timeout in milliseconds, applied to every operation until cleared.
    /// A caller narrows it for one action and restores it afterwards; interactions
    /// fall back to the driver's short interaction default when this is null.
    /// </summary>
    double? TimeoutOverrideMs { get; set; }

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
