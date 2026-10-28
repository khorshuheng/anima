namespace Anima.Dashboard.Service.Tests;

public sealed class DashboardOptionsTests
{
    [Fact]
    public void Defaults_point_at_the_reserved_local_ports()
    {
        var options = new DashboardOptions();

        Assert.StartsWith("http://127.0.0.1:", options.WorkflowBaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("http://127.0.0.1:", options.KnowledgeBaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("http://127.0.0.1:", options.BusBaseUrl, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(10), options.UpstreamTimeout);
    }

    [Fact]
    public void Validator_accepts_a_complete_configuration()
    {
        var result = new DashboardOptionsValidator().Validate(null, new DashboardOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:7411")]
    [InlineData("ftp://127.0.0.1:7411")]
    public void Validator_rejects_a_base_url_that_is_not_absolute_http(string baseUrl)
    {
        var result = new DashboardOptionsValidator().Validate(
            null,
            new DashboardOptions { KnowledgeBaseUrl = baseUrl });

        Assert.False(result.Succeeded);
        var failures = Assert.IsType<IEnumerable<string>>(result.Failures, exactMatch: false);
        Assert.Contains(failures, failure => failure.Contains("KnowledgeBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_a_non_positive_timeout()
    {
        var result = new DashboardOptionsValidator().Validate(
            null,
            new DashboardOptions { UpstreamTimeout = TimeSpan.Zero });

        Assert.False(result.Succeeded);
        var failures = Assert.IsType<IEnumerable<string>>(result.Failures, exactMatch: false);
        Assert.Contains(failures, failure => failure.Contains("UpstreamTimeout", StringComparison.Ordinal));
    }
}
