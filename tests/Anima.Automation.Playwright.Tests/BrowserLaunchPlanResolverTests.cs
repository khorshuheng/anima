namespace Anima.Automation.Playwright.Tests;

public sealed class BrowserLaunchPlanResolverTests
{
    private const string ChromePath = "/usr/bin/google-chrome";

    [Fact]
    public void Launch_is_the_default_mode()
    {
        var plan = Resolve();

        Assert.Equal(BrowserSessionMode.Launch, plan.Mode);
        Assert.Equal("chrome", plan.BrowserName);
        Assert.True(plan.OwnsBrowser);
        Assert.False(plan.UsesSharedContext);
        Assert.True(plan.SupportsCdp);
        Assert.Null(plan.UserDataDirectory);
        Assert.Null(plan.ConnectEndpoint);
    }

    [Fact]
    public void Mode_comes_from_the_environment_when_unset()
    {
        var plan = Resolve(environment: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrowserLaunchPlanResolver.ModeEnvironmentVariable] = "profile",
        });

        Assert.Equal(BrowserSessionMode.Profile, plan.Mode);
        Assert.Equal(BrowserLaunchPlanResolver.DefaultUserDataDirectory("chrome"), plan.UserDataDirectory);
        Assert.True(plan.UsesSharedContext);
    }

    [Fact]
    public void Configured_mode_wins_over_the_environment()
    {
        var plan = Resolve(
            new PlaywrightOptions { Mode = "connect" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BrowserLaunchPlanResolver.ModeEnvironmentVariable] = "profile",
            });

        Assert.Equal(BrowserSessionMode.Connect, plan.Mode);
    }

    [Fact]
    public void Unknown_mode_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve(new PlaywrightOptions { Mode = "headed" }));

        Assert.Contains("headed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(BrowserLaunchPlanResolver.ModeEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_defaults_to_an_anima_owned_directory()
    {
        var plan = Resolve(new PlaywrightOptions { Mode = "profile" });

        Assert.Equal(BrowserSessionMode.Profile, plan.Mode);
        Assert.NotNull(plan.UserDataDirectory);
        Assert.Contains("anima", plan.UserDataDirectory, StringComparison.Ordinal);
        Assert.EndsWith("chrome-profile", plan.UserDataDirectory, StringComparison.Ordinal);

        // Never the user's real Chrome profile: it is locked by their browser and
        // refuses remote debugging on Chrome 136+.
        Assert.DoesNotContain("google-chrome", plan.UserDataDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_honours_an_explicit_directory()
    {
        var plan = Resolve(new PlaywrightOptions
        {
            Mode = "profile",
            UserDataDirectory = "/var/tmp/anima-profile",
        });

        Assert.Equal("/var/tmp/anima-profile", plan.UserDataDirectory);
    }

    [Fact]
    public void Profile_expands_a_home_prefixed_directory()
    {
        var plan = Resolve(new PlaywrightOptions
        {
            Mode = "profile",
            UserDataDirectory = "~/anima-profile",
        });

        Assert.NotNull(plan.UserDataDirectory);
        Assert.DoesNotContain("~", plan.UserDataDirectory, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            plan.UserDataDirectory,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_rejects_a_connect_endpoint()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(new PlaywrightOptions
        {
            Mode = "profile",
            ConnectEndpoint = BrowserLaunchPlanResolver.DefaultConnectEndpoint,
        }));

        Assert.Contains("Mode=profile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connect_defaults_to_the_conventional_cdp_port()
    {
        var plan = Resolve(new PlaywrightOptions { Mode = "connect" });

        Assert.Equal(BrowserSessionMode.Connect, plan.Mode);
        Assert.NotNull(plan.ConnectEndpoint);
        Assert.Equal(new Uri(BrowserLaunchPlanResolver.DefaultConnectEndpoint), plan.ConnectEndpoint);
        Assert.False(plan.OwnsBrowser);
        Assert.True(plan.UsesSharedContext);
    }

    [Fact]
    public void Connect_honours_an_explicit_endpoint()
    {
        var plan = Resolve(new PlaywrightOptions
        {
            Mode = "connect",
            ConnectEndpoint = "http://127.0.0.1:9223",
        });

        Assert.Equal(new Uri("http://127.0.0.1:9223"), plan.ConnectEndpoint);
    }

    [Fact]
    public void Connect_accepts_a_websocket_endpoint()
    {
        // Playwright documents endpointURL as "A CDP websocket endpoint or http url",
        // for example the webSocketDebuggerUrl that /json/version reports.
        var plan = Resolve(new PlaywrightOptions
        {
            Mode = "connect",
            ConnectEndpoint = "ws://127.0.0.1:9222/devtools/browser/387adf4c-243f-4051-a181-46798f4a46f4",
        });

        Assert.Equal(
            new Uri("ws://127.0.0.1:9222/devtools/browser/387adf4c-243f-4051-a181-46798f4a46f4"),
            plan.ConnectEndpoint);
    }

    [Fact]
    public void Connect_endpoint_must_be_an_absolute_url()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve(new PlaywrightOptions { Mode = "connect", ConnectEndpoint = "127.0.0.1:9222" }));

        Assert.Contains("127.0.0.1:9222", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Connect_does_not_require_a_local_browser()
    {
        // A machine that only attaches to a remote CDP endpoint has no local Chrome.
        var plan = Resolve(new PlaywrightOptions { Mode = "connect" }, locator: _ => null);

        Assert.Equal(BrowserSessionMode.Connect, plan.Mode);
        Assert.Null(plan.ExecutablePath);
    }

    [Fact]
    public void Configuration_can_be_validated_without_an_installed_browser()
    {
        var plan = BrowserLaunchPlanResolver.Resolve(
            new PlaywrightOptions { Mode = "profile" },
            environment: _ => null,
            executableLocator: _ => null,
            requireExecutable: false);

        Assert.Equal(BrowserSessionMode.Profile, plan.Mode);
        Assert.Null(plan.ExecutablePath);
    }

    [Fact]
    public void Connect_rejects_a_user_data_directory()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(new PlaywrightOptions
        {
            Mode = "connect",
            UserDataDirectory = "/var/tmp/anima-profile",
        }));

        Assert.Contains("Mode=connect", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_rejects_a_user_data_directory()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(new PlaywrightOptions
        {
            UserDataDirectory = "/var/tmp/anima-profile",
        }));

        Assert.Contains("mode profile", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public void Non_chromium_engine_cannot_use_a_shared_mode(string browserName)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(
            new PlaywrightOptions { BrowserName = browserName, Mode = "profile" },
            locator: _ => null));

        Assert.Contains("Chromium-family", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Firefox_launch_has_no_cdp()
    {
        var plan = Resolve(
            new PlaywrightOptions { BrowserName = "firefox" },
            locator: _ => null);

        Assert.False(plan.SupportsCdp);
        Assert.Null(plan.ExecutablePath);

        // Firefox always runs a Playwright-managed build, never a channel.
        Assert.Null(plan.Channel);
    }

    [Fact]
    public void Chrome_is_headed_unless_headless_is_requested()
    {
        // Headed is the default so the operator can watch the run; CI opts in.
        Assert.False(Resolve().Headless);
        Assert.True(Resolve(new PlaywrightOptions { Headless = true }).Headless);
    }

    [Fact]
    public void Https_error_policy_reaches_the_plan()
    {
        Assert.True(Resolve().IgnoreHttpsErrors);
        Assert.False(Resolve(new PlaywrightOptions { IgnoreHttpsErrors = false }).IgnoreHttpsErrors);
    }

    [Fact]
    public void System_executable_wins_over_a_channel()
    {
        var plan = Resolve(new PlaywrightOptions { Channel = "chrome" });

        Assert.Equal(ChromePath, plan.ExecutablePath);
        Assert.Null(plan.Channel);
    }

    [Fact]
    public void Missing_chrome_executable_is_reported()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve(locator: _ => null));

        Assert.Contains("ANIMA_CHROME_PATH", exception.Message, StringComparison.Ordinal);
    }

    private static BrowserLaunchPlan Resolve(
        PlaywrightOptions? options = null,
        Dictionary<string, string>? environment = null,
        Func<string, string?>? locator = null)
    {
        locator ??= _ => ChromePath;

        return BrowserLaunchPlanResolver.Resolve(
            options,
            name => environment is not null && environment.TryGetValue(name, out var value) ? value : null,
            locator);
    }
}
