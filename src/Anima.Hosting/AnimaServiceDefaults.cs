using Anima.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Anima.Hosting;

public static class AnimaServiceDefaults
{
    /// <summary>
    /// Shared host configuration: local URL, problem details, health checks,
    /// OpenAPI and service metadata. Every Anima service uses this so an agent
    /// can discover any service the same way.
    /// </summary>
    public static WebApplicationBuilder AddAnimaServiceDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls($"http://127.0.0.1:{defaultPort}");
        }

        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks();
        builder.Services.AddOpenApi();
        builder.Services.AddSingleton(AnimaServiceInfo.For(serviceName, defaultPort));
        return builder;
    }

    /// <summary>Maps the common endpoints and pipeline shared by all services.</summary>
    public static WebApplication UseAnimaServiceDefaults(this WebApplication app, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        app.UseExceptionHandler();

        app.MapGet("/", (AnimaServiceInfo info) => Results.Ok(new
        {
            service = info.Name,
            version = info.Version,
            health = "/health",
            openapi = "/openapi/v1.json",
        }));

        app.MapHealthChecks("/health");
        app.MapHealthChecks("/api/health");
        app.MapOpenApi();
        return app;
    }

    /// <summary>
    /// Points the runtime contract catalog at the repo's compiled descriptors.
    /// Agents can add contracts and run <c>make contracts</c> without rebuilding C#.
    /// </summary>
    public static IServiceCollection AddRepoContracts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddAnimaContracts(options =>
        {
            options.DescriptorDirectory = RepoLayout.DescriptorDirectory;
            options.WatchForChanges = true;
        });
    }
}
