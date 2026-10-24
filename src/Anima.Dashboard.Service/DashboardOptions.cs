using Anima.Hosting;
using Microsoft.Extensions.Options;

namespace Anima.Dashboard.Service;

/// <summary>
/// Dashboard upstream addresses and the deadline applied to proxied reads. Bound from
/// configuration and validated at startup by <see cref="DashboardOptionsValidator"/>.
/// </summary>
public sealed record DashboardOptions
{
    public const string SectionName = "Anima:Dashboard";

    public string WorkflowBaseUrl { get; init; } = $"http://127.0.0.1:{AnimaPorts.Workflow}";

    public string KnowledgeBaseUrl { get; init; } = $"http://127.0.0.1:{AnimaPorts.Knowledge}";

    public string BusBaseUrl { get; init; } = $"http://127.0.0.1:{AnimaPorts.Bus}";

    /// <summary>Deadline for one buffered upstream request, headers and body included.</summary>
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Rejects unusable dashboard configuration at startup instead of failing on the first
/// proxied request, so a bad base URL or a non-positive timeout stops the host.
/// </summary>
public sealed class DashboardOptionsValidator : IValidateOptions<DashboardOptions>
{
    public ValidateOptionsResult Validate(string? name, DashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        ValidateBaseUrl(errors, nameof(DashboardOptions.WorkflowBaseUrl), options.WorkflowBaseUrl);
        ValidateBaseUrl(errors, nameof(DashboardOptions.KnowledgeBaseUrl), options.KnowledgeBaseUrl);
        ValidateBaseUrl(errors, nameof(DashboardOptions.BusBaseUrl), options.BusBaseUrl);
        if (options.UpstreamTimeout <= TimeSpan.Zero)
        {
            errors.Add(
                $"{nameof(DashboardOptions.UpstreamTimeout)} must be positive, got {options.UpstreamTimeout}.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateBaseUrl(List<string> errors, string property, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{property} must be an absolute http(s) URL, got '{value}'.");
        }
    }
}
