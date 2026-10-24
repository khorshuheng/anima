using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Knowledge.Sqlite;

public static class SqliteKnowledgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite knowledge graph (production default). In-memory
    /// remains available for tests via <c>AddInMemoryKnowledgeGraph</c>.
    /// </summary>
    public static IServiceCollection AddSqliteKnowledgeGraph(
        this IServiceCollection services,
        Action<SqliteKnowledgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SqliteKnowledgeOptions>();
        services.AddOptions<KnowledgeOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<SqliteKnowledgeGraph>();
        services.TryAddSingleton<IKnowledgeGraph>(provider =>
            provider.GetRequiredService<SqliteKnowledgeGraph>());
        return services;
    }
}
