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
            return new PlaywrightBrowserSession(context, page, cdpSession, options, _options.EventBufferSize);
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
            return new PlaywrightBrowserSession(ownedContext: null, page, cdpSession, options, _options.EventBufferSize);
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
        int eventBufferSize)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        _ownedContext = ownedContext;
        _page = page;
        _options = options;
        _events = new BrowserEventLog(eventBufferSize);
        _responseWaiter = new BrowserResponseWaiter(_events);
        _cdpTransport = cdpSession is null ? null : new PlaywrightCdpTransport(cdpSession);

        _capture = PlaywrightEventCapture.Attach(page, _events, options);
    }

    public string Id { get; } = Guid.NewGuid().ToString("n");

    public string? CurrentUrl => _page.Url;

    public ICdpTransport? Cdp => _cdpTransport;

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

        var timeout = matcher.Timeout ?? TimeSpan.FromMilliseconds(_options.TimeoutMs);
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

    public async Task ClickAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).ClickAsync();
    }

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

    public async Task SelectAsync(string target, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Locator(target).SelectOptionAsync(value);
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

    public async Task<string> TakeScreenshotAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = path ?? $"screenshot-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.png";
        await _page.ScreenshotAsync(new PageScreenshotOptions { Path = resolved, FullPage = true });
        return resolved;
    }

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
