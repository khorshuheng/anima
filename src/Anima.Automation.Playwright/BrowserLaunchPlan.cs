namespace Anima.Automation.Playwright;

/// <summary>
/// How the driver obtains the Chrome instance its sessions run in.
/// </summary>
public enum BrowserSessionMode
{
    /// <summary>
    /// Playwright launches a throwaway Chrome; every session gets its own context
    /// and an optional trace. Nothing survives a restart.
    /// </summary>
    Launch,

    /// <summary>
    /// Playwright launches Chrome with a persistent user data directory, so
    /// cookies and logins survive restarts. All sessions share one context.
    /// </summary>
    Profile,

    /// <summary>
    /// Playwright attaches to a Chrome that is already running with
    /// <c>--remote-debugging-port</c>. That browser owns its profile; the driver
    /// never closes it. All sessions share one context.
    /// </summary>
    Connect,
}

/// <summary>
/// Resolved, validated browser configuration. Produced by
/// <see cref="BrowserLaunchPlanResolver"/> so the driver reads config once and
/// conflicting options fail at startup instead of mid-script.
/// </summary>
public sealed record BrowserLaunchPlan
{
    public required BrowserSessionMode Mode { get; init; }

    public required string BrowserName { get; init; }

    /// <summary>Playwright channel; null when an explicit executable is used.</summary>
    public string? Channel { get; init; }

    /// <summary>System browser executable; null means a Playwright-managed build.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Persistent user data directory. Profile mode only.</summary>
    public string? UserDataDirectory { get; init; }

    /// <summary>CDP endpoint such as <c>http://127.0.0.1:9222</c>. Connect mode only.</summary>
    public Uri? ConnectEndpoint { get; init; }

    public bool Headless { get; init; } = true;

    /// <summary>Certificate policy applied to contexts the driver creates.</summary>
    public bool IgnoreHttpsErrors { get; init; } = true;

    /// <summary>Connect mode attaches to a browser this process did not start.</summary>
    public bool OwnsBrowser => Mode != BrowserSessionMode.Connect;

    /// <summary>Profile and connect modes share a single context across sessions.</summary>
    public bool UsesSharedContext => Mode != BrowserSessionMode.Launch;

    /// <summary>Raw CDP is only available on Chromium-family engines.</summary>
    public bool SupportsCdp => BrowserLaunchPlanResolver.IsChromiumFamily(BrowserName);
}
