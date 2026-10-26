namespace Anima.Automation.Playwright.Tests;

public sealed class BrowserResponseWaiterTests
{
    private static BrowserEvent Response(string requestId, int status = 201, string url = "http://uat.local/api/orders")
        => new()
        {
            Kind = BrowserEventKind.Response,
            RequestId = requestId,
            Method = "POST",
            Url = url,
            Status = status,
            Body = "{\"id\":42}",
        };

    private static BrowserEvent Console() => new()
    {
        Kind = BrowserEventKind.Console,
        Level = "log",
        Text = "noise",
    };

    [Fact]
    public async Task Returns_a_response_that_was_already_recorded()
    {
        // The click step returns before its response arrives, so the common case is
        // "the write already happened before the wait started".
        var log = new BrowserEventLog(50);
        log.Append(Response("r1"));

        var waiter = new BrowserResponseWaiter(log);
        var match = await waiter.WaitAsync(new ResponseMatcher { Method = "POST" }, Timeout());

        Assert.Equal("r1", match.RequestId);
        Assert.Equal(201, match.Status);
    }

    [Fact]
    public async Task Waits_for_a_response_recorded_after_the_call()
    {
        var log = new BrowserEventLog(50);
        var waiter = new BrowserResponseWaiter(log);

        var pending = waiter.WaitAsync(new ResponseMatcher { Status = 201 }, Timeout());
        log.Append(Console());
        log.Append(Response("r1"));

        Assert.Equal("r1", (await pending).RequestId);
    }

    [Fact]
    public async Task Ignores_responses_that_do_not_match()
    {
        var log = new BrowserEventLog(50);
        var waiter = new BrowserResponseWaiter(log);

        var pending = waiter.WaitAsync(new ResponseMatcher { UrlPattern = "*/api/orders" }, Timeout());
        log.Append(Response("r1", url: "http://uat.local/api/users"));
        log.Append(Response("r2", url: "http://uat.local/api/orders"));

        Assert.Equal("r2", (await pending).RequestId);
    }

    [Fact]
    public async Task Each_response_satisfies_only_one_wait()
    {
        // Two identical writes must be confirmable separately.
        var log = new BrowserEventLog(50);
        log.Append(Response("r1"));

        var waiter = new BrowserResponseWaiter(log);

        Assert.Equal("r1", (await waiter.WaitAsync(new ResponseMatcher { Method = "POST" }, Timeout())).RequestId);

        var second = waiter.WaitAsync(new ResponseMatcher { Method = "POST" }, Timeout());
        log.Append(Response("r2"));

        Assert.Equal("r2", (await second).RequestId);
    }

    [Fact]
    public async Task Times_out_when_nothing_matches()
    {
        var log = new BrowserEventLog(50);
        var waiter = new BrowserResponseWaiter(log);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => waiter.WaitAsync(new ResponseMatcher { Status = 204 }, TimeSpan.FromMilliseconds(50)));

        Assert.Contains("status=204", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_timeout_is_rejected_with_a_clear_error(int timeoutMs)
    {
        var log = new BrowserEventLog(50);
        var waiter = new BrowserResponseWaiter(log);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => waiter.WaitAsync(new ResponseMatcher(), TimeSpan.FromMilliseconds(timeoutMs)));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var log = new BrowserEventLog(50);
        var waiter = new BrowserResponseWaiter(log);
        using var cancel = new CancellationTokenSource();

        var pending = waiter.WaitAsync(new ResponseMatcher(), TimeSpan.FromSeconds(30), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_completed_log_fails_the_wait_instead_of_hanging()
    {
        var log = new BrowserEventLog(50);
        var waiter = new BrowserResponseWaiter(log);

        var pending = waiter.WaitAsync(new ResponseMatcher(), TimeSpan.FromSeconds(30));
        log.Complete();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    /// <summary>Fails a stuck wait instead of hanging the test run.</summary>
    private static TimeSpan Timeout() => TimeSpan.FromSeconds(5);
}
