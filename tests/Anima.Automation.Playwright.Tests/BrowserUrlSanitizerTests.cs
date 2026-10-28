namespace Anima.Automation.Playwright.Tests;

public sealed class BrowserUrlSanitizerTests
{
    [Theory]
    [InlineData("http://uat.local/echo?token=supersecret", "http://uat.local/echo?token=redacted")]
    [InlineData("http://uat.local/cb?code=abc123&state=s1", "http://uat.local/cb?code=redacted&state=s1")]
    [InlineData("http://uat.local/x?api_key=k&page=2", "http://uat.local/x?api_key=redacted&page=2")]
    [InlineData("https://h/p?TOKEN=abc", "https://h/p?TOKEN=redacted")]
    [InlineData("https://h/p?access_token=a&refresh_token=b", "https://h/p?access_token=redacted&refresh_token=redacted")]
    public void Credential_query_parameters_are_redacted(string input, string expected) => Assert.Equal(expected, BrowserUrlSanitizer.Sanitize(input));

    [Theory]
    [InlineData("http://uat.local/page.html")]
    [InlineData("http://uat.local/page.html?page=2")]
    [InlineData("http://uat.local/page.html?query=token")]
    [InlineData("data:text/html,<h1>hi</h1>")]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_credential_urls_are_untouched(string? input) => Assert.Equal(input, BrowserUrlSanitizer.Sanitize(input));

    [Fact]
    public void Fragments_are_preserved()
    {
        Assert.Equal(
            "http://uat.local/cb?token=redacted#done",
            BrowserUrlSanitizer.Sanitize("http://uat.local/cb?token=abc#done"));
    }

    [Fact]
    public void Valueless_parameters_are_still_redacted() => Assert.Equal("http://uat.local/x?token=redacted", BrowserUrlSanitizer.Sanitize("http://uat.local/x?token"));

    [Fact]
    public void Percent_encoded_names_match() => Assert.Equal("http://uat.local/x?tok%65n=redacted", BrowserUrlSanitizer.Sanitize("http://uat.local/x?tok%65n=abc"));
}
