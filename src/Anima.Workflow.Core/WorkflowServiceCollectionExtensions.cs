using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Workflow;

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the graph-backed workflow store, bus event publisher and bus
    /// dispatcher. The host must register an IKnowledgeGraph and IBusPublisher.
    /// No agent runtime, process launcher or git helper is created here.
    /// </summary>
    public static IServiceCollection AddWorkflowCore(
        this IServiceCollection services,
        Action<WorkflowOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<WorkflowOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IWorkflowStore, KnowledgeWorkflowStore>();
        services.TryAddSingleton<IWorkflowEventPublisher, BusWorkflowEventPublisher>();
        services.TryAddSingleton<IWorkflowDispatcher, BusWorkflowDispatcher>();
        return services;
    }
}
