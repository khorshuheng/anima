namespace Anima.Automation.Playwright;

/// <summary>
/// Turns <see cref="PlaywrightOptions"/> plus environment into a validated
/// <see cref="BrowserLaunchPlan"/>. Deliberately free of Playwright and of file
/// system writes so configuration errors are unit-testable.
/// </summary>
public static class BrowserLaunchPlanResolver
{
    public const string ModeEnvironmentVariable = "ANIMA_BROWSER_MODE";

    public const string ProfileEnvironmentVariable = "ANIMA_CHROME_PROFILE";

    public const string EndpointEnvironmentVariable = "ANIMA_CDP_ENDPOINT";

    /// <summary>Conventional CDP port; Chrome itself has no default, so this is ours.</summary>
    public const string DefaultConnectEndpoint = "http://127.0.0.1:9222";

    private const string LaunchModeName = "launch";
    private const string ProfileModeName = "profile";
    private const string ConnectModeName = "connect";

    private static readonly string[] ChromiumFamily = ["chrome", "chromium", "msedge"];

    private static readonly string[] SupportedBrowsers = ["chrome", "chromium", "msedge", "firefox", "webkit"];

    public static bool IsChromiumFamily(string browserName)
        => ChromiumFamily.Contains(browserName, StringComparer.Ordinal);

    /// <summary>Validates configuration into a launch plan.</summary>
    /// <param name="options">Bound configuration; null means all defaults.</param>
    /// <param name="environment">Environment lookup, defaults to the process environment.</param>
    /// <param name="executableLocator">System browser lookup, defaults to <see cref="BrowserExecutableLocator"/>.</param>
    /// <param name="requireExecutable">
    /// False validates configuration only, so a service can fail fast at startup
    /// without demanding that Chrome is already installed.
    /// </param>
    public static BrowserLaunchPlan Resolve(
        PlaywrightOptions? options,
        Func<string, string?>? environment = null,
        Func<string, string?>? executableLocator = null,
        bool requireExecutable = true)
    {
        var readEnvironment = environment ?? Environment.GetEnvironmentVariable;
        var locateExecutable = executableLocator ?? (name => BrowserExecutableLocator.Find(name));
        options ??= new PlaywrightOptions();

        string browserName = (FirstNonEmpty(options.BrowserName, readEnvironment("ANIMA_BROWSER")) ?? "chrome")
            .ToLowerInvariant();

        if (!SupportedBrowsers.Contains(browserName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported automation browser '{browserName}'. Set " +
                "Anima:Automation:Playwright:BrowserName (or ANIMA_BROWSER) to " +
                "chrome, chromium, msedge, firefox or webkit.");
        }

        string modeName = (FirstNonEmpty(options.Mode, readEnvironment(ModeEnvironmentVariable)) ?? LaunchModeName)
            .ToLowerInvariant();

        if (options.EventBufferSize <= 0)
        {
            throw new InvalidOperationException(
                "Anima:Automation:Playwright:EventBufferSize must be greater than zero.");
        }

        var mode = modeName switch
        {
            LaunchModeName => BrowserSessionMode.Launch,
            ProfileModeName => BrowserSessionMode.Profile,
            ConnectModeName => BrowserSessionMode.Connect,
            _ => throw new InvalidOperationException(
                $"Unknown browser mode '{modeName}'. Set Anima:Automation:Playwright:Mode (or " +
                $"{ModeEnvironmentVariable}) to launch, profile or connect."),
        };

        if (mode != BrowserSessionMode.Launch && !IsChromiumFamily(browserName))
        {
            throw new InvalidOperationException(
                $"Browser mode '{modeName}' needs a Chromium-family browser; '{browserName}' " +
                "cannot run with a persistent profile or be attached over CDP.");
        }

        string? profileDirectory = FirstNonEmpty(
            options.UserDataDirectory,
            readEnvironment(ProfileEnvironmentVariable));

        string? endpointText = FirstNonEmpty(
            options.ConnectEndpoint,
            readEnvironment(EndpointEnvironmentVariable));

        string? userDataDirectory = null;
        Uri? connectEndpoint = null;

        switch (mode)
        {
            case BrowserSessionMode.Profile:
                if (endpointText is not null)
                {
                    throw new InvalidOperationException(
                        "Mode=profile launches its own Chrome and owns the user data directory; " +
                        $"drop ConnectEndpoint (or {EndpointEnvironmentVariable}) or switch to mode connect.");
                }

                userDataDirectory = ExpandHome(profileDirectory ?? DefaultUserDataDirectory(browserName));
                break;

            case BrowserSessionMode.Connect:
                if (profileDirectory is not null)
                {
                    throw new InvalidOperationException(
                        "Mode=connect attaches to a Chrome that owns its own user data directory; " +
                        $"drop UserDataDirectory (or {ProfileEnvironmentVariable}).");
                }

                connectEndpoint = ParseEndpoint(endpointText ?? DefaultConnectEndpoint);
                break;

            default:
                if (profileDirectory is not null)
                {
                    throw new InvalidOperationException(
                        $"UserDataDirectory (or {ProfileEnvironmentVariable}) is only used with " +
                        "mode profile; set Anima:Automation:Playwright:Mode to profile.");
                }

                if (endpointText is not null)
                {
                    throw new InvalidOperationException(
                        $"ConnectEndpoint (or {EndpointEnvironmentVariable}) is only used with " +
                        "mode connect; set Anima:Automation:Playwright:Mode to connect.");
                }

                break;
        }

        // Connect mode never launches anything, so a local Chrome is irrelevant:
        // requiring one would break attaching to a remote CDP endpoint.
        string? executablePath = null;
        string? channel = null;

        if (mode != BrowserSessionMode.Connect)
        {
            // An explicit executable always wins over a channel, which is what
            // keeps Chrome system-installed instead of Playwright-managed.
            executablePath = FirstNonEmpty(options.ExecutablePath) ?? locateExecutable(browserName);

            if (executablePath is null
                && requireExecutable
                && string.Equals(browserName, "chrome", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Chrome was not found in the usual locations. Install Chrome, set " +
                    "Anima:Automation:Playwright:ExecutablePath, or set ANIMA_CHROME_PATH.");
            }

            channel = FirstNonEmpty(options.Channel);
            if (channel is null && executablePath is null && browserName is "chrome" or "msedge")
            {
                channel = browserName;
            }

            if (executablePath is not null)
            {
                channel = null;
            }
        }

        return new BrowserLaunchPlan
        {
            Mode = mode,
            BrowserName = browserName,
            Channel = channel,
            ExecutablePath = executablePath,
            UserDataDirectory = userDataDirectory,
            ConnectEndpoint = connectEndpoint,
            Headless = options.Headless,
            IgnoreHttpsErrors = options.IgnoreHttpsErrors,
        };
    }

    /// <summary>
    /// Anima-owned profile location used when profile mode has no explicit
    /// directory. Never the user's real Chrome profile: that one is locked by
    /// their running browser and refuses remote debugging on Chrome 136+.
    /// </summary>
    public static string DefaultUserDataDirectory(string browserName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserName);

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "No writable root for the default browser profile directory; set " +
                $"Anima:Automation:Playwright:UserDataDirectory (or {ProfileEnvironmentVariable}).");
        }

        return Path.Combine(root, "anima", "automation", $"{browserName}-profile");
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || !IsCdpScheme(endpoint))
        {
            throw new InvalidOperationException(
                $"CDP endpoint '{value}' is not an absolute http(s) or ws(s) URL " +
                $"(for example {DefaultConnectEndpoint}).");
        }

        return endpoint;
    }

    /// <summary>
    /// Playwright accepts either an http URL or a browser websocket endpoint, such
    /// as the webSocketDebuggerUrl that <c>/json/version</c> reports.
    /// </summary>
    private static bool IsCdpScheme(Uri endpoint)
        => endpoint.Scheme == Uri.UriSchemeHttp
            || endpoint.Scheme == Uri.UriSchemeHttps
            || endpoint.Scheme == "ws"
            || endpoint.Scheme == "wss";

    private static string ExpandHome(string path)
    {
        if (path.Length == 0 || path[0] != '~')
        {
            return path;
        }

        if (path.Length > 1 && path[1] != '/' && path[1] != '\\')
        {
            return path;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path[2..]);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
