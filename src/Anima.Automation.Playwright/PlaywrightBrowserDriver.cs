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
            return new PlaywrightBrowserSession(context, page, cdpSession, options, _options.EventBufferSize, _options.ScreenshotDirectory, _options.UploadDirectory);
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
            return new PlaywrightBrowserSession(ownedContext: null, page, cdpSession, options, _options.EventBufferSize, _options.ScreenshotDirectory, _options.UploadDirectory);
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
        var directory = plan.UserDataDirectory!;
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
        string? uploadDirectory)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        _ownedContext = ownedContext;
        _page = page;
        _options = options;
        _defaultTimeoutMs = options.TimeoutMs;
        _screenshotDirectory = screenshotDirectory;
        _uploadDirectory = uploadDirectory;
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
            _page.SetDefaultTimeout((float)value);
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

        await _page.Locator(target).ClickAsync(new LocatorClickOptions
        {
            Button = options?.Button is { } button ? ParseMouseButton(button) : null,
            ClickCount = options is null ? null : options.ClickCount,
            Modifiers = options?.Modifiers?.Select(ParseModifier).ToArray(),
            Position = ToPosition(options?.Position),
        });
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

        await _page.Locator(target).DblClickAsync(new LocatorDblClickOptions
        {
            Button = options?.Button is { } button ? ParseMouseButton(button) : null,
            Modifiers = options?.Modifiers?.Select(ParseModifier).ToArray(),
            Position = ToPosition(options?.Position),
        });
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

        await locator.PressSequentiallyAsync(
            value,
            new LocatorPressSequentiallyOptions { Delay = options?.DelayMs });
    }

    public async Task ClearAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).ClearAsync();
    }

    public async Task HoverAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).HoverAsync();
    }

    public async Task CheckAsync(
        string target,
        bool isChecked = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).SetCheckedAsync(isChecked);
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
            await _page.Locator(target).HoverAsync();
        }

        await _page.Mouse.WheelAsync(deltaX, deltaY);
    }

    public async Task ScrollIntoViewAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).ScrollIntoViewIfNeededAsync();
    }

    public async Task DragToAsync(string source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(source).DragToAsync(_page.Locator(target));
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
            var root = Path.GetFullPath(_uploadDirectory);
            var candidate = Path.GetFullPath(path);

            if (!candidate.StartsWith(
                    root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"upload source '{path}' is outside the configured upload directory '{root}'");
            }
        }

        await _page.Locator(target).SetInputFilesAsync(path);
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
        await _page.Locator(target).FillAsync(value);
    }

    public async Task PressAsync(string target, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).PressAsync(key);
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
        await _page.Locator(target).SelectOptionAsync(value);
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
        var resolved = await locator.EvaluateAsync<string?[]>(
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

        var missing = values.Where((value, index) => resolved[index] is null).ToArray();

        if (missing.Length > 0)
        {
            var available = await locator.EvaluateAllAsync<string[]>(
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
        return values.ToArray();
    }

    public async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _page.EvaluateAsync(expression);
        return value?.ToString() ?? string.Empty;
    }

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
        var cursor = _events.LatestSequence;

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
                var actual = await _page.Locator(target).CountAsync();
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

        var directory = _screenshotDirectory ?? DefaultScreenshotDirectory;
        Directory.CreateDirectory(directory);

        var path = options.Path ?? Path.Combine(
            directory,
            (options.Name ?? $"shot-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}") + ".png");

        var documentHeight = await _page.EvaluateAsync<int>("document.documentElement.scrollHeight");
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
            var width = await _page.EvaluateAsync<int>("window.innerWidth");
            var height = options.MaxHeight is { } cap ? Math.Min(documentHeight, cap) : documentHeight;

            // Clip only reaches past the viewport when fullPage is set as well;
            // without it the capture silently came back viewport-sized.
            content = await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = path,
                FullPage = true,
                Clip = new Clip { X = 0, Y = 0, Width = width, Height = height },
            });
        }

        var read = PngSize.TryRead(content, out var pixelWidth, out var pixelHeight);

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
