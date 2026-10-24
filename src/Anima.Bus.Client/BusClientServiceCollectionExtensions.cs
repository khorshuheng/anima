using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Bus.Client;

public static class BusClientServiceCollectionExtensions
{
    /// <summary>Registers the HTTP bus client (publish + SSE subscribe + mailbox).</summary>
    public static IServiceCollection AddHttpBus(
        this IServiceCollection services,
        Action<BusClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<BusClientOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient("anima-bus", (provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<BusClientOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.TryAddSingleton<HttpMessageBus>(provider => new HttpMessageBus(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("anima-bus"),
            provider.GetRequiredService<IOptions<BusClientOptions>>(),
            provider.GetRequiredService<ILogger<HttpMessageBus>>()));

        services.TryAddSingleton<IMessageBus>(provider => provider.GetRequiredService<HttpMessageBus>());
        services.TryAddSingleton<IBusPublisher>(provider => provider.GetRequiredService<HttpMessageBus>());
        services.TryAddSingleton<IBusSubscriber>(provider => provider.GetRequiredService<HttpMessageBus>());
        services.TryAddSingleton<IBusRequester, BusRequester>();
        services.TryAddSingleton<IAgentMailboxFactory, HttpAgentMailboxFactory>();
        return services;
    }
}
