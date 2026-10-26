namespace Anima.Automation.Playwright.Tests;

public sealed class BrowserUrlResolverTests
{
    private const string BaseUrl = "http://127.0.0.1:8080/app/";

    [Theory]
    [InlineData("/login")]
    [InlineData("/login?next=%2Fhome")]
    public void Rooted_paths_resolve_against_the_base_url(string url)
    {
        Assert.Equal("http://127.0.0.1:8080/login" + Suffix(url), BrowserUrlResolver.Resolve(url, BaseUrl));
    }

    [Fact]
    public void Relative_paths_resolve_against_the_base_path()
    {
        Assert.Equal("http://127.0.0.1:8080/app/page", BrowserUrlResolver.Resolve("page", BaseUrl));
    }

    [Fact]
    public void Rooted_paths_are_not_mistaken_for_file_uris()
    {
        // The trap: Uri.TryCreate reports "/login" as an absolute file: URI, which
        // used to short-circuit base-URL resolution and break /actions in the
        // shared-context modes.
        Assert.True(Uri.TryCreate("/login", UriKind.Absolute, out var parsed));
        Assert.True(parsed!.IsFile);

        Assert.Equal("https://uat.example/login", BrowserUrlResolver.Resolve("/login", "https://uat.example"));
    }

    [Fact]
    public void Absolute_urls_are_returned_verbatim()
    {
        const string url = "data:text/html,<h1 id=t>hi</h1>";

        Assert.Equal(url, BrowserUrlResolver.Resolve(url, BaseUrl));
    }

    [Theory]
    [InlineData("data:text/html,<h1>hi</h1>")]
    [InlineData("about:blank")]
    [InlineData("https://example.test/x")]
    public void Non_file_schemes_are_never_rebased(string url)
    {
        Assert.Equal(url, BrowserUrlResolver.Resolve(url, BaseUrl));
    }

    [Fact]
    public void Resolved_urls_are_escaped()
    {
        Assert.Equal("http://127.0.0.1:8080/app/a%20b", BrowserUrlResolver.Resolve("a b", BaseUrl));
    }

    [Fact]
    public void Relative_urls_without_a_base_are_returned_unchanged()
    {
        Assert.Equal("/login", BrowserUrlResolver.Resolve("/login", baseUrl: null));
    }

    [Fact]
    public void Empty_targets_are_returned_unchanged()
    {
        Assert.Equal(string.Empty, BrowserUrlResolver.Resolve(string.Empty, BaseUrl));
    }

    private static string Suffix(string url)
        => url["/login".Length..];
}
