using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Automation.Playwright;

public static class PlaywrightServiceCollectionExtensions
{
    public static IServiceCollection AddPlaywrightAutomation(
        this IServiceCollection services,
        Action<PlaywrightOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<PlaywrightOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IBrowserDriver, PlaywrightBrowserDriver>();
        return services;
    }
}
