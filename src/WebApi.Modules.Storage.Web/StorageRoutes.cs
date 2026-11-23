using Carter;
using soft.square.net.WebApi.Modules.Storage.Web.Access;
using soft.square.net.WebApi.Modules.Storage.Web.Items;
using soft.square.net.WebApi.Modules.Storage.Web.Uploads;

namespace soft.square.net.WebApi.Modules.Storage.Web;

/// <summary>
/// The module's route table and its only Carter module (the runtime maps it under <c>/api/storage</c>). Every route
/// needs <c>storage.access</c> or <c>storage.admin</c>, except the signed download links (anonymous: the signature is
/// the authorization) and group administration (<c>storage.admin</c>).
/// </summary>
public sealed class StorageRoutes : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var storage = app.MapGroup("").AddEndpointFilter<StorageProblemsFilter>();

        var users = storage.MapGroup("").RequirePermission(StoragePermissions.Access, StoragePermissions.Admin);
        ItemEndpoints.Map(users);
        UploadEndpoints.Map(users);
        AccessEndpoints.Map(users);

        AccessEndpoints.MapAdmin(storage.MapGroup("").RequirePermission(StoragePermissions.Admin));

        ItemEndpoints.MapLinks(storage);
    }
}
