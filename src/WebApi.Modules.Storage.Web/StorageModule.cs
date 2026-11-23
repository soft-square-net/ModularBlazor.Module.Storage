using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Contracts;
using soft.square.net.WebApi.Modules.Storage.Web.Configurations;

namespace soft.square.net.WebApi.Modules.Storage.Web;

/// <summary>
/// Storage: the tenant's folders and files, Windows-style. The tenant database holds the tree (schema "storage"), the
/// bytes are blobs in a store (the local disk, or S3 / MinIO through named connections). Access is an NTFS-style access
/// control list on every folder and file, for users, everyone, holders of a platform permission and Storage groups;
/// the platform permissions <c>storage.access</c> and <c>storage.admin</c> only open the door.
/// This is the composition root: the runtime creates it, reads the manifest and calls <see cref="ConfigureServices"/>
/// on the module's own container; the endpoints are in <see cref="StorageRoutes"/>.
/// </summary>
public sealed class StorageModule : IRuntimeModule
{
    public ModuleManifest Manifest { get; } = new("storage", "Storage", "0.1.0", "storage")
    {
        Description = "Folders and files with Windows-style access control lists, resumable uploads and signed download links.",
        DefaultSettings = new Dictionary<string, string?>
        {
            ["Storage:MaxFileSize"] = (2L * 1024 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Storage:ChunkSize"] = (8 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Storage:UploadLifetime"] = "1.00:00:00",
            ["Storage:DownloadLinkLifetime"] = "00:10:00",
            ["Storage:MaxCopyItems"] = "10000",
            ["Storage:Local:Root"] = ""
        },
        Permissions = StoragePermissions.All
    };

    public void ConfigureServices(IServiceCollection services, IConfiguration settings) => services.AddServiceConfigs(settings);
}
