using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Client;

public static class KnowledgeClientServiceCollectionExtensions
{
    /// <summary>Registers an HTTP-backed knowledge graph against a memgraph-compatible service.</summary>
    public static IServiceCollection AddKnowledgeClient(
        this IServiceCollection services,
        Action<KnowledgeClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<KnowledgeClientOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<IKnowledgeGraph, HttpKnowledgeGraph>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<KnowledgeClientOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(options.SessionId))
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation(options.SessionHeader, options.SessionId);
            }
        });

        return services;
    }
}
