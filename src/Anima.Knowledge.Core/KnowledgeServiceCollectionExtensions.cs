using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Knowledge;

public static class KnowledgeServiceCollectionExtensions
{
    /// <summary>Registers the in-memory task/memory graph.</summary>
    public static IServiceCollection AddInMemoryKnowledgeGraph(
        this IServiceCollection services,
        Action<KnowledgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<KnowledgeOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(_ => KnowledgeVocabularyLoader.LoadCore());

        services.TryAddSingleton<InMemoryKnowledgeGraph>();
        services.TryAddSingleton<IKnowledgeGraph>(provider => provider.GetRequiredService<InMemoryKnowledgeGraph>());
        return services;
    }
}
