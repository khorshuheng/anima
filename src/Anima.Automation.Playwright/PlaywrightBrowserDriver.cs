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

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _cdpSupported;

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

        var browser = await GetBrowserAsync(cancellationToken);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = options.BaseUrl,
            IgnoreHTTPSErrors = options.IgnoreHttpsErrors,
        });

        if (!string.IsNullOrWhiteSpace(options.TracePath))
        {
            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true,
            });
        }

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)options.TimeoutMs);

        // CDP exists only for Chromium-family browsers. Other engines still get a
        // full session; the Cdp property is null and callers must handle that.
        ICDPSession? cdpSession = null;
        if (_cdpSupported)
        {
            cdpSession = await context.NewCDPSessionAsync(page);
        }

        _logger.LogInformation(
            "Started Playwright session for {BaseUrl} (headless={Headless}, cdp={Cdp}).",
            options.BaseUrl ?? "<none>",
            options.Headless,
            _cdpSupported);

        return new PlaywrightBrowserSession(context, page, cdpSession, options.TracePath);
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return _browser;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is not null)
            {
                return _browser;
            }

            _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();
            var browserName = ResolveBrowserName();
            var executablePath = ResolveExecutablePath(browserName);
            var channel = _options.Channel;
            IBrowserType browserType;

            switch (browserName)
            {
                case "chromium":
                    browserType = _playwright.Chromium;
                    _cdpSupported = true;
                    break;
                case "chrome":
                    browserType = _playwright.Chromium;
                    if (executablePath is null)
                    {
                        channel ??= "chrome";
                    }

                    _cdpSupported = true;
                    break;
                case "msedge":
                    browserType = _playwright.Chromium;
                    if (executablePath is null)
                    {
                        channel ??= "msedge";
                    }

                    _cdpSupported = true;
                    break;
                case "firefox":
                    browserType = _playwright.Firefox;
                    _cdpSupported = false;
                    break;
                case "webkit":
                    browserType = _playwright.Webkit;
                    _cdpSupported = false;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported automation browser '{browserName}'. " +
                        "Set Anima:Automation:Playwright:BrowserName (or ANIMA_BROWSER) to " +
                        "chrome, chromium, msedge, firefox or webkit.");
            }

            // An explicit executable always wins over a Playwright-managed build
            // or a browser channel; that is what keeps Chrome system-installed.
            if (executablePath is not null)
            {
                channel = null;
            }

            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless,
                Channel = channel,
                ExecutablePath = executablePath,
                SlowMo = _options.SlowMoMs > 0 ? (float)_options.SlowMoMs : null,
            };

            _logger.LogInformation(
                "Launching {Browser} (executable={ExecutablePath}, channel={Channel}).",
                browserName,
                executablePath ?? "<playwright-managed>",
                channel ?? "<none>");

            _browser = await browserType.LaunchAsync(launchOptions);
            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveBrowserName()
    {
        var configured = _options.BrowserName;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("ANIMA_BROWSER");
        }

        return string.IsNullOrWhiteSpace(configured)
            ? "chrome"
            : configured.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Finds the system browser for Chromium-family engines. Returning null lets
    /// Playwright resolve its own build (required for firefox/webkit, optional
    /// for chromium).
    /// </summary>
    private string? ResolveExecutablePath(string browserName)
    {
        if (!string.IsNullOrWhiteSpace(_options.ExecutablePath))
        {
            return _options.ExecutablePath;
        }

        var located = BrowserExecutableLocator.Find(browserName);
        if (located is not null)
        {
            return located;
        }

        if (browserName == "chrome")
        {
            throw new InvalidOperationException(
                "Chrome was not found in the usual locations. Install Chrome, set " +
                "Anima:Automation:Playwright:ExecutablePath, or set ANIMA_CHROME_PATH.");
        }

        // Firefox/webkit always use Playwright-managed builds; chromium falls back to one.
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _gate.Dispose();
    }
}

internal sealed class PlaywrightBrowserSession : IBrowserSession
{
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly string? _tracePath;
    private readonly PlaywrightCdpTransport? _cdpTransport;

    public PlaywrightBrowserSession(
        IBrowserContext context,
        IPage page,
        ICDPSession? cdpSession,
        string? tracePath)
    {
        _context = context;
        _page = page;
        _tracePath = tracePath;
        _cdpTransport = cdpSession is null ? null : new PlaywrightCdpTransport(cdpSession);
    }

    public string Id { get; } = Guid.NewGuid().ToString("n");

    public string? CurrentUrl => _page.Url;

    public ICdpTransport? Cdp => _cdpTransport;

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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
        if (!string.IsNullOrWhiteSpace(_tracePath))
        {
            await _context.Tracing.StopAsync(new TracingStopOptions { Path = _tracePath });
        }

        await _context.CloseAsync();
        await _context.DisposeAsync();
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
