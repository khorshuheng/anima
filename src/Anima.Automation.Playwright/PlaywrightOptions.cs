namespace Anima.Automation.Playwright;

public sealed class PlaywrightOptions
{
    /// <summary>
    /// Browser family: chrome (default), chromium, msedge, firefox or webkit.
    /// Empty falls back to ANIMA_BROWSER. Chrome uses the installed executable.
    /// </summary>
    public string? BrowserName { get; set; } = "chrome";

    /// <summary>Optional channel such as <c>chrome</c> or <c>msedge</c>.</summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Optional explicit browser executable. When empty, Chrome/Chromium/Edge
    /// are discovered from the system (ANIMA_CHROME_PATH wins) so no Playwright
    /// browser download is needed.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Slows down operations; useful when debugging a script.</summary>
    public double SlowMoMs { get; set; }

    /// <summary>Browser launch mode. Sessions cannot override this per-session.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>Browser download directory override used by install scripts.</summary>
    public string? BrowserCacheDirectory { get; set; }
}
