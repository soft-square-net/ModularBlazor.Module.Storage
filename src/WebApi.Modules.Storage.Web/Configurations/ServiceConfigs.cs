using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Modules.Storage.Infrastructure;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Behaviors;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;
using soft.square.net.WebApi.Modules.Storage.Web.Extensions;

namespace soft.square.net.WebApi.Modules.Storage.Web.Configurations;

public static class ServiceConfigs
{
    /// <summary>
    /// Everything the module registers, in the module's own container (never the host's): settings, data access, the
    /// blob provider, the access services the use cases share, signed download links, and the mediator.
    /// </summary>
    public static IServiceCollection AddServiceConfigs(this IServiceCollection services, IConfiguration settings)
    {
        services.Configure<StorageOptions>(settings.GetSection("Storage"));
        services.AddInfrastructureServices(settings);

        services.AddScoped<StorageCaller>();
        services.AddScoped<ItemAccess>();
        services.AddSingleton<IDownloadTickets, DownloadTickets>();

        services.AddMediator(options =>
        {
            // Scoped: handlers use the request's tenant DbContext and the caller's access scopes.
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.Assemblies = [typeof(ListFolderQuery)];
            options.PipelineBehaviors = [typeof(LoggingBehavior<,>)];
        });
        return services;
    }
}
