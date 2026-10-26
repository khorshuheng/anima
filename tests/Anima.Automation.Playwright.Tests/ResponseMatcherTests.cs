namespace Anima.Automation.Playwright.Tests;

public sealed class GlobPatternTests
{
    [Theory]
    [InlineData("http://uat.local/api/orders", "http://uat.local/api/*", true)]
    [InlineData("http://uat.local/api/orders", "*api/orders*", true)]
    [InlineData("http://uat.local/api/orders", "*/orders", true)]
    [InlineData("http://uat.local/api/orders", "http://uat.local/api/orders", true)]
    [InlineData("http://uat.local/api/orders", "http://uat.local/api/users", false)]
    [InlineData("http://uat.local/api/orders", "*/users", false)]
    [InlineData("HTTP://UAT.local/Orders", "*orders", true)]
    [InlineData("http://uat.local/a", "http://uat.local/?", true)]
    [InlineData("http://uat.local/ab", "http://uat.local/?", false)]
    [InlineData("http://uat.local/x", "*", true)]
    [InlineData("http://uat.local/x", null, true)]
    [InlineData(null, "*", false)]
    [InlineData("", "*", true)]
    public void Glob_matches_expected_urls(string? value, string? pattern, bool expected)
    {
        Assert.Equal(expected, GlobPattern.IsMatch(value, pattern));
    }
}

public sealed class ResponseMatcherTests
{
    private static BrowserEvent Response(
        string method = "POST",
        string url = "http://uat.local/api/orders",
        int status = 201) => new()
        {
            Kind = BrowserEventKind.Response,
            Method = method,
            Url = url,
            Status = status,
        };

    [Fact]
    public void Empty_matcher_matches_any_response()
    {
        Assert.True(new ResponseMatcher().Matches(Response()));
    }

    [Fact]
    public void Non_response_events_never_match()
    {
        var request = Response() with { Kind = BrowserEventKind.Request, Status = null };

        Assert.False(new ResponseMatcher().Matches(request));
    }

    [Fact]
    public void Method_matches_case_insensitively()
    {
        Assert.True(new ResponseMatcher { Method = "post" }.Matches(Response(method: "POST")));
        Assert.False(new ResponseMatcher { Method = "PUT" }.Matches(Response(method: "POST")));
    }

    [Fact]
    public void Status_must_match_exactly()
    {
        Assert.True(new ResponseMatcher { Status = 201 }.Matches(Response(status: 201)));
        Assert.False(new ResponseMatcher { Status = 200 }.Matches(Response(status: 201)));
    }

    [Fact]
    public void Url_pattern_filters_by_glob()
    {
        Assert.True(new ResponseMatcher { UrlPattern = "*/api/orders" }.Matches(Response()));
        Assert.False(new ResponseMatcher { UrlPattern = "*/api/users" }.Matches(Response()));
    }

    [Fact]
    public void Fields_combine_with_and()
    {
        var matcher = new ResponseMatcher { Method = "POST", Status = 201, UrlPattern = "*orders" };

        Assert.True(matcher.Matches(Response()));
        Assert.False(matcher.Matches(Response(status: 500)));
    }

    [Fact]
    public void Description_lists_only_set_fields()
    {
        Assert.Equal("any response", new ResponseMatcher().ToString());
        Assert.Equal("method=POST, status=201", new ResponseMatcher { Method = "POST", Status = 201 }.ToString());
    }
}
