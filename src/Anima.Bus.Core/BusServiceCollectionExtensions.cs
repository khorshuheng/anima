using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Bus;

public static class BusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process bus. Remote (broker or HTTP) implementations can
    /// replace <see cref="IMessageBus"/> without changing agent code.
    /// </summary>
    public static IServiceCollection AddInProcBus(
        this IServiceCollection services,
        Action<BusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<BusOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<InProcMessageBus>();
        services.TryAddSingleton<IMessageBus>(provider => provider.GetRequiredService<InProcMessageBus>());
        services.TryAddSingleton<IBusPublisher>(provider => provider.GetRequiredService<InProcMessageBus>());
        services.TryAddSingleton<IBusSubscriber>(provider => provider.GetRequiredService<InProcMessageBus>());
        services.TryAddSingleton<IBusDiagnostics>(provider => provider.GetRequiredService<InProcMessageBus>());
        services.TryAddSingleton<IAgentMailboxFactory, InProcAgentMailboxFactory>();
        services.TryAddSingleton<IBusRequester, BusRequester>();
        return services;
    }
}
