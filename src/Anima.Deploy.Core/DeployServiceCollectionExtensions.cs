using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Deploy;

public static class DeployServiceCollectionExtensions
{
    public static IServiceCollection AddDeployHooks(
        this IServiceCollection services,
        Action<DeployOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<DeployOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<GitPushPayloadParser>();
        services.TryAddSingleton<DeployDispatcher>();
        services.TryAddSingleton<IDeployRunStore, InMemoryDeployRunStore>();
        services.TryAddSingleton<IDeployScriptRunner, ProcessDeployScriptRunner>();
        services.TryAddSingleton<IDeployHookSink, DeployHookSink>();
        services.AddHostedService<DeployWorker>();
        return services;
    }
}
