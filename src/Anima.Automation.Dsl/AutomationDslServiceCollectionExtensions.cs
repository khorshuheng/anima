using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Automation.Dsl;

public static class AutomationDslServiceCollectionExtensions
{
    public static IServiceCollection AddYamlAutomationScripts(
        this IServiceCollection services,
        Action<ScriptCatalogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ScriptCatalogOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<AutomationScriptParser>();
        services.TryAddSingleton<ScriptCatalog>();
        services.TryAddSingleton<IScriptRunner, YamlAutomationScriptRunner>();
        return services;
    }
}
