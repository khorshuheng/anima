using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Anima.Automation.Playwright;

/// <summary>
/// Playwright-backed driver exposed only through <see cref="IBrowserDriver"/>.
/// No stealth patches are applied: this targets dev/UAT pages that the team owns.
/// </summary>
public sealed class PlaywrightBrowserDriver : IBrowserDriver, IAsyncDisposable
{
    private readonly PlaywrightOptions _options;
    private readonly ILogger<PlaywrightBrowserDriver> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly string[] PersistentProfileArgs = ["--no-first-run", "--no-default-browser-check"];

    private IPlaywright? _playwright;
    private BrowserHost? _host;
    private bool _disposed;

    public PlaywrightBrowserDriver(
        IOptions<PlaywrightOptions> options,
        ILogger<PlaywrightBrowserDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "playwright";

    public async Task<IBrowserSession> StartSessionAsync(
        SessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var host = await GetHostAsync(cancellationToken);

        if (options.Headless != _options.Headless)
        {
            // The browser launch mode is service-wide, so a per-session override
            // cannot be honoured once contexts are shared. Say so rather than
            // silently ignoring it.
            _logger.LogWarning(
                "Ignoring per-session headless={Requested}; the browser is launched headless={Actual}.",
                options.Headless,
                _options.Headless);
        }

        if (host.Plan.UsesSharedContext && options.IgnoreHttpsErrors != host.Plan.IgnoreHttpsErrors)
        {
            _logger.LogWarning(
                "Ignoring per-session ignoreHttpsErrors={Requested}; browser mode {Mode} uses " +
                "ignoreHttpsErrors={Actual} from configuration.",
                options.IgnoreHttpsErrors,
                host.Plan.Mode,
                host.Plan.IgnoreHttpsErrors);
        }

        return host.Plan.UsesSharedContext
            ? await StartSharedSessionAsync(host, options)
            : await StartOwnedSessionAsync(host, options);
    }

    private async Task<IBrowserSession> StartOwnedSessionAsync(BrowserHost host, SessionOptions options)
    {
        var context = await host.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = options.BaseUrl,
            IgnoreHTTPSErrors = options.IgnoreHttpsErrors,
        });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)options.TimeoutMs);

            if (!string.IsNullOrWhiteSpace(options.TracePath))
            {
                await context.Tracing.StartAsync(new TracingStartOptions
                {
                    Screenshots = true,
                    Snapshots = true,
                    Sources = true,
                });
            }

            var cdpSession = await TryCreateCdpSessionAsync(host, context, page);
            LogSessionStarted(host, options, cdpSession);
            return new PlaywrightBrowserSession(context, page, cdpSession, options, _options.EventBufferSize, _options.ScreenshotDirectory, _options.UploadDirectory, _options.InteractionTimeoutMs);
        }
        catch (Exception)
        {
            // A half-built session must not leave a context behind in the browser.
            await context.CloseAsync();
            await context.DisposeAsync();
            throw;
        }
    }

    private async Task<IBrowserSession> StartSharedSessionAsync(BrowserHost host, SessionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TracePath))
        {
            throw new NotSupportedException(
                $"Tracing needs a per-session context, which browser mode '{host.Plan.Mode}' " +
                "does not have. Unset TracePath or use mode launch.");
        }

        var context = host.SharedContext!;
        var page = await context.NewPageAsync();

        try
        {
            page.SetDefaultTimeout((float)options.TimeoutMs);
            var cdpSession = await TryCreateCdpSessionAsync(host, context, page);
            LogSessionStarted(host, options, cdpSession);
            return new PlaywrightBrowserSession(ownedContext: null, page, cdpSession, options, _options.EventBufferSize, _options.ScreenshotDirectory, _options.UploadDirectory, _options.InteractionTimeoutMs);
        }
        catch (Exception)
        {
            // Shared context: drop only this session's page.
            await page.CloseAsync();
            throw;
        }
    }

    /// <summary>
    /// CDP exists only for Chromium-family browsers. Other engines still get a full
    /// session; the Cdp property is null and callers must handle that.
    /// </summary>
    private static async Task<ICDPSession?> TryCreateCdpSessionAsync(
        BrowserHost host,
        IBrowserContext context,
        IPage page)
        => host.Plan.SupportsCdp ? await context.NewCDPSessionAsync(page) : null;

    private void LogSessionStarted(BrowserHost host, SessionOptions options, ICDPSession? cdpSession)
        => _logger.LogInformation(
            "Started browser session for {BaseUrl} (mode={Mode}, cdp={Cdp}, sharedContext={Shared}).",
            options.BaseUrl ?? "<none>",
            host.Plan.Mode,
            cdpSession is not null,
            host.Plan.UsesSharedContext);

    private async Task<BrowserHost> GetHostAsync(CancellationToken cancellationToken)
    {
        if (_host is { IsUsable: true })
        {
            return _host;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_host is { IsUsable: false })
            {
                // The browser behind the host is gone: a connected Chrome was
                // closed, or an owned one crashed. Drop the stale host instead of
                // failing every later session until the process restarts.
                _logger.LogWarning(
                    "Browser for mode {Mode} is disconnected; recreating it.",
                    _host.Plan.Mode);

                await DisposeHostAsync(_host);
                _host = null;
            }

            _host ??= await CreateHostAsync(cancellationToken);
            return _host;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisposeHostAsync(BrowserHost host)
    {
        try
        {
            await host.DisposeAsync();
        }
        catch (PlaywrightException exception)
        {
            // Shutdown and recovery must survive an already-dead browser.
            _logger.LogWarning(exception, "Failed to close the browser cleanly.");
        }
    }

    private async Task<BrowserHost> CreateHostAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve and validate configuration before starting the Playwright driver,
        // so a config error does not pay for spawning node first.
        var plan = BrowserLaunchPlanResolver.Resolve(_options);
        _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();
        var browserType = ResolveBrowserType(_playwright, plan.BrowserName);

        _logger.LogInformation(
            "Browser mode {Mode}: browser={Browser}, executable={ExecutablePath}, channel={Channel}, " +
            "headless={Headless}, ignoreHttpsErrors={IgnoreHttpsErrors}, profile={ProfileDirectory}, " +
            "endpoint={ConnectEndpoint}.",
            plan.Mode,
            plan.BrowserName,
            plan.ExecutablePath ?? "<playwright-managed>",
            plan.Channel ?? "<none>",
            plan.Headless,
            plan.IgnoreHttpsErrors,
            plan.UserDataDirectory ?? "<none>",
            plan.ConnectEndpoint?.AbsoluteUri ?? "<none>");

        switch (plan.Mode)
        {
            case BrowserSessionMode.Connect:
                // The attached Chrome already owns its profile, its contexts and its
                // certificate policy; say so instead of silently ignoring config.
                if (plan.IgnoreHttpsErrors)
                {
                    _logger.LogInformation(
                        "Connect mode uses the certificate policy of the Chrome at {Endpoint}; " +
                        "IgnoreHttpsErrors is not applied there.",
                        plan.ConnectEndpoint?.AbsoluteUri);
                }

                return await AttachAsync(browserType, plan);

            case BrowserSessionMode.Profile:
                return await LaunchWithProfileAsync(browserType, plan);

            default:
                var browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = plan.Headless,
                    Channel = plan.Channel,
                    ExecutablePath = plan.ExecutablePath,
                    SlowMo = SlowMo,
                });

                return new BrowserHost(plan, browser, sharedContext: null);
        }
    }

    private static async Task<BrowserHost> AttachAsync(IBrowserType browserType, BrowserLaunchPlan plan)
    {
        var browser = await browserType.ConnectOverCDPAsync(plan.ConnectEndpoint!.AbsoluteUri);

        // Reuse the default context of the running Chrome; create one only when
        // it has none yet (a freshly started browser).
        var shared = browser.Contexts.Count > 0
            ? browser.Contexts[0]
            : await browser.NewContextAsync();

        return new BrowserHost(plan, browser, shared);
    }

    private async Task<BrowserHost> LaunchWithProfileAsync(IBrowserType browserType, BrowserLaunchPlan plan)
    {
        string directory = plan.UserDataDirectory!;
        Directory.CreateDirectory(directory);

        var context = await browserType.LaunchPersistentContextAsync(
            directory,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = plan.Headless,
                Channel = plan.Channel,
                ExecutablePath = plan.ExecutablePath,
                SlowMo = SlowMo,
                IgnoreHTTPSErrors = plan.IgnoreHttpsErrors,
                Args = PersistentProfileArgs,
            });

        return new BrowserHost(plan, context.Browser, context);
    }

    private float? SlowMo => _options.SlowMoMs > 0 ? (float)_options.SlowMoMs : null;

    private static IBrowserType ResolveBrowserType(IPlaywright playwright, string browserName)
        => browserName switch
        {
            "firefox" => playwright.Firefox,
            "webkit" => playwright.Webkit,
            _ => playwright.Chromium,
        };

    public async ValueTask DisposeAsync()
    {
        // Dispose is idempotent: DI shutdown and explicit tests may both call it.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Take the gate so shutdown cannot race an in-flight CreateHostAsync.
        await _gate.WaitAsync();
        try
        {
            if (_host is not null)
            {
                await DisposeHostAsync(_host);
                _host = null;
            }
        }
        finally
        {
            _gate.Release();
            _playwright?.Dispose();
            _playwright = null;
            _gate.Dispose();
        }
    }
}

/// <summary>
/// The one browser instance behind the driver. Launch mode owns a browser and
/// lets each session create its own context; profile and connect modes own or
/// attach to a single context that every session shares as a page.
/// </summary>
internal sealed class BrowserHost(BrowserLaunchPlan plan, IBrowser? browser, IBrowserContext? sharedContext)
    : IAsyncDisposable
{
    public BrowserLaunchPlan Plan { get; } = plan;

    public IBrowser? Browser { get; } = browser;

    public IBrowserContext? SharedContext { get; } = sharedContext;

    /// <summary>
    /// False once the browser behind this host is gone. Playwright reports that for
    /// a connected Chrome the operator closed as well as for an owned one that
    /// crashed, and both need the host rebuilt.
    /// </summary>
    public bool IsUsable => Browser is null || Browser.IsConnected;

    public async ValueTask DisposeAsync()
    {
        // Connect mode never closes what it attached to: that Chrome belongs to
        // whoever started it, profile and windows included.
        if (!Plan.OwnsBrowser)
        {
            return;
        }

        if (Plan.Mode == BrowserSessionMode.Profile)
        {
            // Closing a persistent context also shuts its browser down.
            if (SharedContext is not null)
            {
                await SharedContext.CloseAsync();
            }

            return;
        }

        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }
    }
}

internal sealed class PlaywrightBrowserSession : IBrowserSession
{
    private readonly IBrowserContext? _ownedContext;
    private readonly IPage _page;
    private readonly SessionOptions _options;
    private readonly BrowserEventLog _events;
    private readonly BrowserResponseWaiter _responseWaiter;
    private readonly PageEventCapture _capture;
    private readonly string? _screenshotDirectory;
    private readonly string? _uploadDirectory;
    private double _defaultTimeoutMs;
    private double? _timeoutOverrideMs;
    private int _interactionDepth;
    private readonly double _interactionTimeoutMs;
    private readonly PlaywrightCdpTransport? _cdpTransport;

    /// <summary>
    /// Creates a session over a page. <paramref name="ownedContext"/> is the
    /// session's own context in launch mode; null means the context is shared and
    /// the session owns only its page.
    /// </summary>
    public PlaywrightBrowserSession(
        IBrowserContext? ownedContext,
        IPage page,
        ICDPSession? cdpSession,
        SessionOptions options,
        int eventBufferSize,
        string? screenshotDirectory,
        string? uploadDirectory,
        double interactionTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        _ownedContext = ownedContext;
        _page = page;
        _options = options;
        _defaultTimeoutMs = options.TimeoutMs;
        _screenshotDirectory = screenshotDirectory;
        _uploadDirectory = uploadDirectory;
        _interactionTimeoutMs = interactionTimeoutMs;
        _events = new BrowserEventLog(eventBufferSize);
        _responseWaiter = new BrowserResponseWaiter(_events);
        _cdpTransport = cdpSession is null ? null : new PlaywrightCdpTransport(cdpSession);

        _capture = PlaywrightEventCapture.Attach(page, _events, options);
    }

    /// <summary>Absolute default so a service working directory cannot swallow artifacts.</summary>
    private static string DefaultScreenshotDirectory
        => Path.Combine(Path.GetTempPath(), "anima", "screenshots");

    public string Id { get; } = Guid.NewGuid().ToString("n");

    public string? CurrentUrl => _page.Url;

    public ICdpTransport? Cdp => _cdpTransport;

    public double DefaultTimeoutMs
    {
        get => _defaultTimeoutMs;
        set
        {
            _defaultTimeoutMs = value;
            ApplyPageTimeout();
        }
    }

    public double? TimeoutOverrideMs
    {
        get => _timeoutOverrideMs;
        set
        {
            _timeoutOverrideMs = value;
            ApplyPageTimeout();
        }
    }

    /// <summary>
    /// A selector-based interaction waits at most the override or the driver's short
    /// interaction default, so a wrong selector fails in seconds; waits opt out of
    /// this scope and use their own timeout.
    /// </summary>
    private double EffectivePageTimeout
        => _timeoutOverrideMs ?? (_interactionDepth > 0 ? _interactionTimeoutMs : _defaultTimeoutMs);

    private void ApplyPageTimeout() => _page.SetDefaultTimeout((float)EffectivePageTimeout);

    private InteractionScope BeginInteraction()
    {
        _interactionDepth++;
        ApplyPageTimeout();
        return new InteractionScope(this);
    }

    private readonly struct InteractionScope(PlaywrightBrowserSession session) : IDisposable
    {
        private readonly PlaywrightBrowserSession _session = session;

        public void Dispose()
        {
            _session._interactionDepth--;
            _session._interactionDepth = Math.Max(0, _session._interactionDepth);
            _session.ApplyPageTimeout();
        }
    }

    /// <summary>
    /// Runs a locator action inside the interaction timeout and, when it times out,
    /// names the elements that look like what the caller meant.
    /// </summary>
    /// <summary>
    /// Explains a timed-out interaction: a selector that matched nothing gets
    /// near-match suggestions, while one that matched but could not be acted on says
    /// so, so nobody hunts for a typo that is not there.
    /// </summary>
    private async Task<string> DescribeFailureHintAsync(string selector)
    {
        try
        {
            int matched = await _page.Locator(selector).CountAsync().ConfigureAwait(false);

            return matched > 0
                ? $"matched {matched} element(s), but the action never became possible " +
                    "(not visible, enabled, stable, or not receiving events)\n"
                : await DescribeNearMatchesAsync(selector).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private async Task InteractAsync(string selector, Func<Task> action)
    {
        using var scope = BeginInteraction();

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"{await DescribeFailureHintAsync(selector).ConfigureAwait(false)}{exception.Message}");
        }
    }

    private async Task<T> InteractAsync<T>(string selector, Func<Task<T>> action)
    {
        using var scope = BeginInteraction();

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"{await DescribeFailureHintAsync(selector).ConfigureAwait(false)}{exception.Message}");
        }
    }

    /// <summary>
    /// Looks for elements whose id, name, aria-label or placeholder resembles a token
    /// of the failed selector, so "element not found" also says what probably exists.
    /// </summary>
    private async Task<string> DescribeNearMatchesAsync(string selector)
    {
        try
        {
            string[] matches = await _page.EvaluateAsync<string[]>(NearMatchScript, selector).ConfigureAwait(false);
            return matches.Length == 0
                ? string.Empty
                : $"near matches: {string.Join(", ", matches)}\n";
        }
        catch (PlaywrightException)
        {
            // Diagnostics must never mask the original failure.
            return string.Empty;
        }
    }

    public long DroppedEventCount => _events.DroppedCount;

    public IReadOnlyList<BrowserEvent> ReadEvents(long afterSequence = 0, int limit = 200)
        => _events.Read(afterSequence, limit);

    public IAsyncEnumerable<BrowserEvent> SubscribeEventsAsync(
        long afterSequence = 0,
        CancellationToken cancellationToken = default)
        => _events.SubscribeAsync(afterSequence, cancellationToken);

    public async Task<BrowserEvent> WaitForResponseAsync(
        ResponseMatcher matcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        var timeout = matcher.Timeout ?? TimeSpan.FromMilliseconds(_defaultTimeoutMs);
        var response = await _responseWaiter.WaitAsync(matcher, timeout, cancellationToken).ConfigureAwait(false);

        // A caller that asks for the body gets it even when the session did not opt
        // into capturing every response body.
        if (matcher.IncludeBody && response.Body is null && response.RequestId is not null)
        {
            var body = await _capture.ReadBodyAsync(response.RequestId).ConfigureAwait(false);

            if (body.Text is { } text)
            {
                response = response with { Body = text.Length > 4096 ? text[..4096] : text };
            }
            else if (body.SkippedBytes is { } skipped)
            {
                // Same cap as the capture path, so the DSL can say why it is missing.
                response = response with { Detail = $"body-skipped:{skipped} bytes" };
            }
        }

        return response;
    }

    /// <summary>
    /// Navigates the session page. Relative targets are resolved against the
    /// session base URL by <see cref="BrowserUrlResolver"/>, which is the only
    /// resolution step in the shared-context modes.
    /// </summary>
    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.GotoAsync(
            BrowserUrlResolver.Resolve(url, _options.BaseUrl),
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    public async Task ClickAsync(
        string target,
        BrowserClickOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await InteractAsync(target, () => _page.Locator(target).ClickAsync(new LocatorClickOptions
        {
            Button = options?.Button is { } button ? ParseMouseButton(button) : null,
            ClickCount = options?.ClickCount,
            Modifiers = options?.Modifiers?.Select(ParseModifier).ToArray(),
            Position = ToPosition(options?.Position),
        }));
    }

    private static Position? ToPosition(BrowserPosition? position)
        => position is null
            ? null
            : new Position { X = (float)position.X, Y = (float)position.Y };

    /// <summary>Double click. Grid cells (ag-grid) open their editor on this.</summary>
    public async Task DoubleClickAsync(
        string target,
        BrowserClickOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await InteractAsync(target, () => _page.Locator(target).DblClickAsync(new LocatorDblClickOptions
        {
            Button = options?.Button is { } button ? ParseMouseButton(button) : null,
            Modifiers = options?.Modifiers?.Select(ParseModifier).ToArray(),
            Position = ToPosition(options?.Position),
        }));
    }

    public async Task TypeAsync(
        string target,
        string value,
        BrowserTypeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        cancellationToken.ThrowIfCancellationRequested();

        var locator = _page.Locator(target);

        // Character by character: widgets such as antd Select filter on every
        // keystroke, which a single fill() never triggers.
        if (options?.ClearFirst == true)
        {
            await locator.ClearAsync();
        }

        await InteractAsync(target, () => locator.PressSequentiallyAsync(
            value,
            new LocatorPressSequentiallyOptions { Delay = options?.DelayMs }));
    }

    public async Task ClearAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).ClearAsync());
    }

    public async Task HoverAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).HoverAsync());
    }

    public async Task CheckAsync(
        string target,
        bool isChecked = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).SetCheckedAsync(isChecked));
    }

    public async Task ScrollAsync(
        int deltaX,
        int deltaY,
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A wheel event over the element is what virtualised grids (ag-grid) and
        // scroll containers listen to; scrollTop assignment often does nothing.
        if (!string.IsNullOrWhiteSpace(target))
        {
            await InteractAsync(target, () => _page.Locator(target).HoverAsync());
        }

        await _page.Mouse.WheelAsync(deltaX, deltaY);
    }

    public async Task ScrollIntoViewAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).ScrollIntoViewIfNeededAsync());
    }

    public async Task DragToAsync(string source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(source, () => _page.Locator(source).DragToAsync(_page.Locator(target)));
    }

    public async Task SetInputFilesAsync(string target, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"upload source '{path}' does not exist on the service host", path);
        }

        // The path comes from the caller, so a configured root limits the read to
        // an operator-chosen directory.
        if (!string.IsNullOrWhiteSpace(_uploadDirectory))
        {
            string root = Path.GetFullPath(_uploadDirectory);
            string candidate = Path.GetFullPath(path);

            if (!candidate.StartsWith(
                    root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"upload source '{path}' is outside the configured upload directory '{root}'");
            }
        }

        await InteractAsync(target, () => _page.Locator(target).SetInputFilesAsync(path));
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.ReloadAsync();
    }

    public async Task GoBackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.GoBackAsync();
    }

    public async Task SetViewportAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        cancellationToken.ThrowIfCancellationRequested();
        await _page.SetViewportSizeAsync(width, height);
    }

    private static MouseButton ParseMouseButton(string button) => button.ToLowerInvariant() switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        "left" => MouseButton.Left,
        _ => throw new InvalidOperationException($"Unknown mouse button '{button}'. Use left, right or middle."),
    };

    private static KeyboardModifier ParseModifier(string modifier) => modifier.ToLowerInvariant() switch
    {
        "alt" => KeyboardModifier.Alt,
        "control" or "ctrl" => KeyboardModifier.Control,
        "meta" or "cmd" or "command" => KeyboardModifier.Meta,
        "shift" => KeyboardModifier.Shift,
        _ => throw new InvalidOperationException(
            $"Unknown click modifier '{modifier}'. Use Alt, Control, Meta or Shift."),
    };

    public async Task FillAsync(string target, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).FillAsync(value));
    }

    public async Task PressAsync(string target, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).PressAsync(key));
    }

    public async Task PressKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Keyboard.PressAsync(key);
    }

    public async Task SelectAsync(string target, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InteractAsync(target, () => _page.Locator(target).SelectOptionAsync(value));
    }

    public async Task SelectAsync(
        string target,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var locator = _page.Locator(target);

        // Resolve each requested value against option values first, then labels, so
        // the multiple form accepts exactly what the single form accepts. Doing it in
        // one evaluate also turns "no such option" into an immediate error instead of
        // a 30s selection timeout that blames the locator.
        string?[] resolved = await locator.EvaluateAsync<string?[]>(
            """
            (element, wanted) => {
                const options = Array.from(element.options || []);
                return wanted.map(value => {
                    const byValue = options.find(option => option.value === value);
                    if (byValue) return byValue.value;
                    const byLabel = options.find(option => (option.label || option.textContent || '') === value);
                    return byLabel ? byLabel.value : null;
                });
            }
            """,
            values.ToArray());

        string[] missing = [.. values.Where((value, index) => resolved[index] is null)];

        if (missing.Length > 0)
        {
            string[] available = await locator.EvaluateAllAsync<string[]>(
                "elements => elements.flatMap(element => Array.from(element.options || []).map(option => option.value))");

            throw new InvalidOperationException(
                $"select: no option matches {string.Join(", ", missing.Select(value => $"'{value}'"))} " +
                $"(available: {string.Join(", ", available)}).");
        }

        await locator.SelectOptionAsync(
            resolved.Select(value => new SelectOptionValue { Value = value! }).ToArray());
    }

    public async Task WaitForAsync(
        string target,
        WaitState state = WaitState.Visible,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waitForState = state switch
        {
            WaitState.Hidden => WaitForSelectorState.Hidden,
            WaitState.Attached => WaitForSelectorState.Attached,
            WaitState.Detached => WaitForSelectorState.Detached,
            _ => WaitForSelectorState.Visible,
        };

        await _page.Locator(target).WaitForAsync(new LocatorWaitForOptions { State = waitForState });
    }

    public async Task<string?> GetTextAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).TextContentAsync();
    }

    public async Task<string?> GetAttributeAsync(
        string target,
        string attribute,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).GetAttributeAsync(attribute);
    }

    public async Task<IReadOnlyList<string>> QueryAllTextAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await _page.Locator(target).AllTextContentsAsync();
        return [.. values];
    }

    public async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _page.EvaluateAsync(expression);
        return value?.ToString() ?? string.Empty;
    }

    public async Task<IReadOnlyList<FormFieldResult>> FillFormAsync(
        IReadOnlyDictionary<string, FormFieldValue> fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<FormFieldResult>(fields.Count);

        foreach (var (selector, value) in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                results.Add(await FillFieldAsync(selector, value).ConfigureAwait(false));
            }
            catch (Exception exception) when (
                exception is TimeoutException or InvalidOperationException or PlaywrightException)
            {
                string done = results.Count == 0
                    ? "no earlier fields were changed"
                    : $"already set: {string.Join(", ", results.Select(field => field.Selector))}";

                throw new InvalidOperationException(
                    $"fillForm failed on '{selector}' ({value}): {exception.Message} ({done})",
                    exception);
            }
        }

        return results;
    }

    private async Task<FormFieldResult> FillFieldAsync(string selector, FormFieldValue value)
    {
        using var scope = BeginInteraction();

        var before = await ReadFieldAsync(selector).ConfigureAwait(false);

        // A boolean aimed at a text control is text: normalise once so the action and
        // the verification compare the same thing.
        if (before.control == "text" && value.Text is null && value.Toggle is { } asToggle)
        {
            value = FormFieldValue.FromText(asToggle ? "true" : "false");
        }

        switch (before.control)
        {
            case "text":
                // A boolean aimed at a text control is still fillable ("true"/"false"),
                // so the parser's kind inference cannot make a field unfillable.
                string? text = (value.Text
                    ?? (value.Toggle is { } toggle ? (toggle ? "true" : "false") : null)) ?? throw new InvalidOperationException("a text control needs a text value");
                await _page.Locator(selector).FillAsync(text).ConfigureAwait(false);
                break;

            case "checkbox":
            case "radio":
            case "antd-checkbox":
            case "antd-radio":
                if (value.Toggle is not { } checkedWanted)
                {
                    throw new InvalidOperationException($"{before.control} needs a boolean value");
                }

                await CheckAsync(selector, checkedWanted).ConfigureAwait(false);
                break;

            case "antd-switch":
                await FillSwitchAsync(selector, value).ConfigureAwait(false);
                break;

            case "select":
            case "select-multiple":
                await FillNativeSelectAsync(selector, value).ConfigureAwait(false);
                break;

            case "antd-select":
                await FillAntdSelectAsync(selector, value).ConfigureAwait(false);
                break;

            case "file":
                throw new InvalidOperationException("use setinputfiles for a file input, not fillForm");

            default:
                throw new InvalidOperationException(
                    $"unsupported control '{before.control}' ({before.tag}). Supported: text inputs, " +
                    "textareas, native selects, checkboxes, radios, antd Select, antd Switch, " +
                    "antd Checkbox and antd Radio.");
        }

        var after = await ReadFieldAsync(selector).ConfigureAwait(false);
        bool verified = Matches(value, after);

        if (!verified)
        {
            throw new InvalidOperationException(
                $"the control reported '{after.value}' (control '{after.control}') after setting it");
        }

        return new FormFieldResult(
            selector,
            after.control,
            value.ToString(),
            after.value,
            after.label);
    }

    private async Task FillSwitchAsync(string selector, FormFieldValue value)
    {
        if (value.Toggle is not { } wanted)
        {
            throw new InvalidOperationException("antd switch needs a boolean value");
        }

        var state = await ReadFieldAsync(selector).ConfigureAwait(false);

        if (state.toggle == wanted)
        {
            return;
        }

        // Clicking the switch toggles it; only click when the state differs.
        await ClickAsync(selector).ConfigureAwait(false);
    }

    private async Task FillNativeSelectAsync(string selector, FormFieldValue value)
    {
        if (value.Choices is { Count: > 0 } choices)
        {
            await SelectAsync(selector, choices).ConfigureAwait(false);
            return;
        }

        if (value.Text is not { } text)
        {
            throw new InvalidOperationException("a select needs a text value or a list of choices");
        }

        await SelectAsync(selector, text).ConfigureAwait(false);
    }

    /// <summary>
    /// antd Select is not a native control: it opens a portalled dropdown, filters as
    /// you type, and keeps the dropdown open while choosing several options.
    /// </summary>
    private async Task FillAntdSelectAsync(string selector, FormFieldValue value)
    {
        var choices = value.Choices ?? (value.Text is { } single ? [single] : []);

        if (choices.Count == 0)
        {
            throw new InvalidOperationException("antd select needs a text value or a list of choices");
        }

        await _page.Locator(selector).First.ClickAsync().ConfigureAwait(false);

        var dropdown = _page.Locator(".ant-select-dropdown:not(.ant-select-dropdown-hidden)").Last;

        foreach (string choice in choices)
        {
            // Clear the filter first: antd usually resets the search input on select,
            // but relying on that would concatenate filters where it does not.
            var search = _page.Locator(".ant-select-focused .ant-select-selection-search-input").First;

            if (await search.CountAsync().ConfigureAwait(false) > 0)
            {
                await search.FillAsync(string.Empty).ConfigureAwait(false);
            }

            // Typing filters the list; rc-select ignores input when not searchable.
            await _page.Keyboard.TypeAsync(choice, new KeyboardTypeOptions { Delay = 20 }).ConfigureAwait(false);

            var option = dropdown.GetByText(choice, new LocatorGetByTextOptions { Exact = true });

            if (await option.CountAsync().ConfigureAwait(false) == 0)
            {
                option = dropdown.Locator(".ant-select-item-option").Filter(
                    new LocatorFilterOptions { HasTextString = choice });
            }

            if (await option.CountAsync().ConfigureAwait(false) == 0)
            {
                var visible = await dropdown.Locator(".ant-select-item-option").AllTextContentsAsync().ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"no option matches '{choice}' (visible: {string.Join(", ", visible.Select(title => title.Trim()))})");
            }

            await option.First.ClickAsync().ConfigureAwait(false);
        }

        if (choices.Count > 1)
        {
            await _page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        }
    }

    /// <summary>Reads every form control under a root, keyed for diffing.</summary>
    public async Task<IReadOnlyList<FormFieldState>> GetFormStateAsync(
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(root))
        {
            int matched = await _page.Locator(root).CountAsync().ConfigureAwait(false);

            if (matched == 0)
            {
                throw new InvalidOperationException($"formstate root '{root}' matched no element");
            }
        }

        // Returned as JSON text: Playwright's object mapping does not preserve the
        // classifier's shape reliably for an array of objects.
        string[] elements = await _page.EvaluateAsync<string[]>(
            $"rootSelector => {{ const classify = {ClassifyScript}; " +
            "const root = document.querySelector(rootSelector) || document.body; " +
            "return [...root.querySelectorAll(" +
            "'input, textarea, select, [contenteditable=true], .ant-switch, .ant-checkbox-wrapper, " +
            ".ant-radio-wrapper, .ant-select')]" +
            // one entry per control: skip the inputs antd hides inside its wrappers,
            // and prefer the ant-select root over its inner search input
            ".filter(element => !(element.tagName === 'INPUT' && " +
            "(element.closest('.ant-checkbox-wrapper') || element.closest('.ant-radio-wrapper'))))" +
            ".filter(element => !element.closest('.ant-select') || element.matches('.ant-select'))" +
            ".map(element => JSON.stringify(classify(element))); }",
            string.IsNullOrWhiteSpace(root) ? "body" : root).ConfigureAwait(false);

        return [.. elements.Select(text =>
        {
            using var document = JsonDocument.Parse(text);
            return ToState(document.RootElement.Clone());
        })];
    }

    /// <summary>Reads one control, used for verification and for single-field checks.</summary>
    private async Task<FormElementState> ReadFieldAsync(string selector)
    {
        var element = await _page.Locator(selector).First.EvaluateAsync<JsonElement>(ClassifyScript).ConfigureAwait(false);
        return ToElementState(element);
    }

    /// <summary>
    /// Classifies one element and reads its current state. Shared by the single-field
    /// read and the whole-form read so they cannot disagree.
    /// </summary>
    private const string ClassifyScript = """
        (element) => {
            const tag = element.tagName.toLowerCase();
            const type = (element.getAttribute('type') || '').toLowerCase();
            const root = (selector) => element.closest(selector);
            const textOf = (node) => (node ? (node.getAttribute('title') || node.textContent || '').trim() : '');
            const inputIn = (scope) => scope ? scope.querySelector('input[type=checkbox], input[type=radio]') : null;
            const label =
                element.getAttribute('aria-label') ||
                (element.labels && element.labels[0] ? element.labels[0].textContent.trim() : '') ||
                textOf(element.closest('label')) ||
                null;
            const key = element.id || element.getAttribute('name') || element.getAttribute('aria-label') || '';

            let control = 'unknown';
            let value = null;
            let toggle = null;
            let choices = null;
            let labels = null;

            if (type === 'file') {
                control = 'file';
            } else if (tag === 'select') {
                control = element.multiple ? 'select-multiple' : 'select';
                choices = [...element.selectedOptions].map(option => option.value);
                labels = [...element.selectedOptions].map(option => (option.label || option.textContent || '').trim());
                value = choices.join(',');
            } else if (type === 'checkbox') {
                control = 'checkbox';
                toggle = element.checked;
                value = String(element.checked);
            } else if (type === 'radio') {
                control = 'radio';
                toggle = element.checked;
                value = String(element.checked);
            } else if (root('.ant-select')) {
                control = 'antd-select';
                choices = [...root('.ant-select').querySelectorAll('.ant-select-selection-item')].map(textOf);
                labels = choices;
                value = choices.join(',');
            } else if (root('.ant-switch')) {
                control = 'antd-switch';
                toggle = root('.ant-switch').getAttribute('aria-checked') === 'true';
                value = String(toggle);
            } else if (root('.ant-checkbox-wrapper') || element.matches('.ant-checkbox')) {
                control = 'antd-checkbox';
                const input = inputIn(root('.ant-checkbox-wrapper')) || element.querySelector('input[type=checkbox]');
                toggle = input ? input.checked : false;
                value = String(toggle);
            } else if (root('.ant-radio-wrapper') || element.matches('.ant-radio')) {
                control = 'antd-radio';
                const input = inputIn(root('.ant-radio-wrapper')) || element.querySelector('input[type=radio]');
                toggle = input ? input.checked : false;
                value = String(toggle);
            } else if (root('.ant-picker')) {
                control = 'antd-picker';
                value = element.value ?? null;
            } else if (root('.ant-cascader')) {
                control = 'antd-cascader';
            } else if (tag === 'input' || tag === 'textarea' || element.isContentEditable) {
                control = 'text';
                value = element.isContentEditable ? element.textContent : element.value;
            }

            return {
                key,
                id: element.id || null,
                name: element.getAttribute('name'),
                label,
                tag,
                type,
                control,
                value,
                toggle,
                choices,
                labels
            };
        }
        """;

    /// <summary>
    /// Finds elements whose id, name, aria-label or placeholder resembles a token of
    /// the selector that matched nothing, so the error can name them.
    /// </summary>
    private const string NearMatchScript = """
        (selector) => {
            const tokens = String(selector)
                .split(/[^A-Za-z0-9_-]+/)
                .map(token => token.trim())
                .filter(token => token.length >= 3)
                .filter(token => !['input', 'select', 'textarea', 'div', 'span', 'button', 'text', 'true', 'false', 'type', 'class', 'aria', 'data'].includes(token.toLowerCase()))
                .map(token => token.toLowerCase());

            if (tokens.length === 0) {
                return [];
            }

            // Edit distance catches typos (transpositions, one wrong letter) that a
            // substring test misses; bounded so it cannot get expensive.
            const distance = (left, right) => {
                if (Math.abs(left.length - right.length) > 3) {
                    return 99;
                }

                const row = [...Array(right.length + 1).keys()];
                for (let i = 1; i <= left.length; i++) {
                    let previous = row[0];
                    row[0] = i;
                    for (let j = 1; j <= right.length; j++) {
                        const current = row[j];
                        row[j] = Math.min(
                            row[j] + 1,
                            row[j - 1] + 1,
                            previous + (left[i - 1] === right[j - 1] ? 0 : 1));
                        previous = current;
                    }
                }

                return row[right.length];
            };

            const words = (value) => value.toLowerCase().split(/[^A-Za-z0-9]+/).filter(word => word.length >= 3);

            const resembles = (value, token) =>
                value.toLowerCase().includes(token) ||
                words(value).some(word => distance(word, token) <= 2);

            const described = new Map();

            for (const element of document.querySelectorAll('[id], [aria-label], [name], [placeholder]')) {
                const fields = [
                    ['id', element.id],
                    ['aria-label', element.getAttribute('aria-label')],
                    ['name', element.getAttribute('name')],
                    ['placeholder', element.getAttribute('placeholder')]
                ].filter(([, value]) => value && value.length > 0);

                for (const [attribute, value] of fields) {
                    if (!tokens.some(token => resembles(value, token))) {
                        continue;
                    }

                    const label = element.id ? `#${element.id}` : `<${element.tagName.toLowerCase()}>`;
                    const detail = attribute === 'id' ? '' : ` ${attribute}="${value}"`;
                    described.set(label, `${label} (${element.tagName.toLowerCase()}${detail})`);
                    break;
                }

                if (described.size >= 5) {
                    break;
                }
            }

            return [...described.values()];
        }
        """;

    private static bool TryRead(JsonElement element, string name, out JsonElement value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? Field(JsonElement element, string name)
        => TryRead(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static FormFieldState ToState(JsonElement element) => new(
        Field(element, "key") ?? string.Empty,
        Field(element, "id"),
        Field(element, "name"),
        Field(element, "label"),
        Field(element, "control") ?? "unknown",
        Field(element, "tag") ?? string.Empty,
        Field(element, "value"),
        Toggle(element, "toggle"),
        Choices(element, "choices"),
        Choices(element, "labels"));

    private static FormElementState ToElementState(JsonElement element) => new(
        Field(element, "control") ?? "unknown",
        Field(element, "tag"),
        Field(element, "value"),
        Toggle(element, "toggle"),
        Field(element, "label"),
        Choices(element, "labels"));

    private static bool? Toggle(JsonElement element, string name)
    {
        if (!TryRead(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string[]? Choices(JsonElement element, string name)
        => TryRead(element, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : null;

    /// <summary>One control's state, as the filler sees it.</summary>
    private sealed record FormElementState(
        string control,
        string? tag,
        string? value,
        bool? toggle,
        string? label,
        IReadOnlyList<string>? labels);

    private static bool Matches(FormFieldValue value, FormElementState state)
    {
        if (value.Toggle is { } toggle)
        {
            return state.toggle == toggle;
        }

        if (value.Choices is { Count: > 0 } choices)
        {
            return choices.All(choice => Selected(state, choice));
        }

        return value.Text is not { } text
            || string.Equals(state.value?.Trim(), text.Trim(), StringComparison.Ordinal)
            || Selected(state, text);
    }

    /// <summary>
    /// True when the control currently holds the requested value or label. Selects
    /// report option values, so a request by label must compare against the labels.
    /// </summary>
    private static bool Selected(FormElementState state, string requested)
        => (state.value?.Contains(requested, StringComparison.OrdinalIgnoreCase) ?? false)
            || (state.labels?.Any(label => string.Equals(label.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase)) ?? false);

    public async Task<string?> GetValueAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).InputValueAsync();
    }

    public async Task<int> CountAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).CountAsync();
    }

    public async Task<string?> GetHtmlAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).InnerHTMLAsync();
    }

    public async Task<string> AriaSnapshotAsync(
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var locator = string.IsNullOrWhiteSpace(target) ? _page.Locator("body") : _page.Locator(target);
        return await locator.AriaSnapshotAsync();
    }

    public async Task<bool> IsVisibleAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).IsVisibleAsync();
    }

    public async Task<bool> IsEnabledAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).IsEnabledAsync();
    }

    public async Task<bool> IsCheckedAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.Locator(target).IsCheckedAsync();
    }

    public async Task<BrowserBox?> GetBoxAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var box = await _page.Locator(target).BoundingBoxAsync();
        return box is null ? null : new BrowserBox(box.X, box.Y, box.Width, box.Height);
    }

    public async Task WaitForUrlAsync(
        string urlPattern,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPattern);
        cancellationToken.ThrowIfCancellationRequested();

        // Cursor first: a navigation that commits between the check below and the
        // subscription must still be replayed, otherwise the wait misses its wakeup.
        long cursor = _events.LatestSequence;

        if (GlobPattern.IsMatch(_page.Url, urlPattern))
        {
            return;
        }

        var timeout = ResolveTimeout(timeoutMs);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            // Driven by the captured navigation events, so the glob semantics match
            // waitForResponse instead of Playwright's URL matcher.
            await foreach (var item in _events.SubscribeAsync(cursor, deadline.Token))
            {
                if (item.Kind == BrowserEventKind.Navigation && GlobPattern.IsMatch(item.Url, urlPattern))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"URL did not match '{urlPattern}' within {timeout.TotalSeconds:0.#}s " +
                $"(current: {_page.Url}).");
        }

        throw new InvalidOperationException(
            $"The session event log ended before the URL matched '{urlPattern}'.");
    }

    public async Task WaitForCountAsync(
        string target,
        int count,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();

        var timeout = ResolveTimeout(timeoutMs);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            if (await _page.Locator(target).CountAsync() == count)
            {
                return;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                int actual = await _page.Locator(target).CountAsync();
                throw new TimeoutException(
                    $"'{target}' had {actual} match(es) after {timeout.TotalSeconds:0.#}s, expected {count}.");
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task WaitForLoadStateAsync(
        BrowserLoadState state = BrowserLoadState.Load,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _page.WaitForLoadStateAsync(state switch
        {
            BrowserLoadState.NetworkIdle => Microsoft.Playwright.LoadState.NetworkIdle,
            BrowserLoadState.DomContentLoaded => Microsoft.Playwright.LoadState.DOMContentLoaded,
            _ => Microsoft.Playwright.LoadState.Load,
        });
    }

    public async Task<BrowserScreenshot> TakeScreenshotAsync(
        BrowserScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BrowserScreenshotOptions();
        cancellationToken.ThrowIfCancellationRequested();

        if (options.MaxHeight is { } requestedCap)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedCap);
        }

        string directory = _screenshotDirectory ?? DefaultScreenshotDirectory;
        Directory.CreateDirectory(directory);

        string path = options.Path ?? Path.Combine(
            directory,
            (options.Name ?? $"shot-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}") + ".png");

        int documentHeight = await _page.EvaluateAsync<int>("document.documentElement.scrollHeight");
        byte[] content;

        if (!string.IsNullOrWhiteSpace(options.Target))
        {
            content = await _page.Locator(options.Target).ScreenshotAsync(
                new LocatorScreenshotOptions { Path = path });
        }
        else if (!options.FullPage)
        {
            content = await _page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
        }
        else
        {
            // Clip rather than scale: without an image library the only way to keep a
            // long page inside a vision budget is to bound the captured height.
            int width = await _page.EvaluateAsync<int>("window.innerWidth");
            int height = options.MaxHeight is { } cap ? Math.Min(documentHeight, cap) : documentHeight;

            // Clip only reaches past the viewport when fullPage is set as well;
            // without it the capture silently came back viewport-sized.
            content = await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = path,
                FullPage = true,
                Clip = new Clip { X = 0, Y = 0, Width = width, Height = height },
            });
        }

        bool read = PngSize.TryRead(content, out int pixelWidth, out int pixelHeight);

        return new BrowserScreenshot
        {
            Path = path,
            Width = read ? pixelWidth : 0,
            Height = read ? pixelHeight : 0,
            Bytes = content.Length,
            FullPage = options.FullPage,
            DocumentHeight = documentHeight,
            // Only a full-page capture can be truncated by the height cap.
            Truncated = options.FullPage && read && documentHeight > pixelHeight,
        };
    }

    /// <summary>
    /// Explicit value wins; otherwise the session default, which per-action narrowing
    /// writes, so one rule covers waits and interactions alike.
    /// </summary>
    private TimeSpan ResolveTimeout(int? timeoutMs)
        => timeoutMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(_defaultTimeoutMs);

    public async ValueTask DisposeAsync()
    {
        // End the event log first: live subscribers (SSE streams) finish instead of
        // waiting forever for events that can no longer arrive.
        _events.Complete();

        if (_ownedContext is null)
        {
            // Shared context: close this session's page and leave the browser and
            // every other session untouched.
            await _page.CloseAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.TracePath))
        {
            await _ownedContext.Tracing.StopAsync(new TracingStopOptions { Path = _options.TracePath });
        }

        await _ownedContext.CloseAsync();
        await _ownedContext.DisposeAsync();
    }
}

internal sealed class PlaywrightCdpTransport(ICDPSession session) : ICdpTransport
{
    public async Task<string> SendAsync(
        string method,
        string? parametersJson = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, object>? parameters = parametersJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);

        var result = await session.SendAsync(method, parameters);
        return result?.GetRawText() ?? "null";
    }
}
