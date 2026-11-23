using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Data;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;
using soft.square.net.WebApi.Modules.Storage.Infrastructure.Data;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure;

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// The module's data access and blob provider, in the module's own container: the tenant DbContext (and through it
    /// <c>IRepository&lt;T&gt;</c> for the aggregates), the store the use cases share, and the local disk provider
    /// (S3 / MinIO come in phase 2).
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration settings)
    {
        services.AddTenantDbContext<StorageDbContext>();
        services.AddScoped<IStorageStore, StorageStore>();

        services.Configure<LocalBlobOptions>(settings.GetSection("Storage:Local"));
        services.AddSingleton<IBlobStore, FileSystemBlobStore>();
        return services;
    }
}
