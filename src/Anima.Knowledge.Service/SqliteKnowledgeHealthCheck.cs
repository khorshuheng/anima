using Anima.Knowledge.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Anima.Knowledge.Service;

internal sealed class SqliteKnowledgeHealthCheck(SqliteKnowledgeGraph graph) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await graph.GetHealthAsync(cancellationToken);
            return HealthCheckResult.Healthy("SQLite knowledge graph reachable");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite knowledge graph failed", exception);
        }
    }
}
