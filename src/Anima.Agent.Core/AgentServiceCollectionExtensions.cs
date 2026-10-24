using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the planner, tool registry, runtime and built-in tools. Swap
    /// <see cref="IAgentPlanner"/> for an LLM-backed implementation without
    /// changing the runtime or the tools.
    /// </summary>
    public static IServiceCollection AddAgentCore(
        this IServiceCollection services,
        Action<AgentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AgentOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IAgentPlanner, DefaultAgentPlanner>();
        services.TryAddSingleton<IToolRegistry, ToolRegistry>();
        services.TryAddSingleton<IAgentRuntime, AgentRuntime>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, KnowledgeRememberTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, KnowledgeSearchTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTool, AutomationScriptTool>());
        return services;
    }
}
