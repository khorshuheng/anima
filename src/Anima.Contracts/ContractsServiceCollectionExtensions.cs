using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Anima.Contracts;

public static class ContractsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runtime contract catalog and codec. The descriptor directory is
    /// resolved from configuration key <c>Anima:Contracts:DescriptorDirectory</c>.
    /// </summary>
    public static IServiceCollection AddAnimaContracts(
        this IServiceCollection services,
        Action<SchemaCatalogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SchemaCatalogOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ISchemaCatalog, FileDescriptorSetCatalog>();
        services.TryAddSingleton<IMessageCodec, DescriptorJsonCodec>();
        return services;
    }
}
