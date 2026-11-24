using Carter;
using soft.square.net.WebApi.Modules.Storage.Web.Access;
using soft.square.net.WebApi.Modules.Storage.Web.Events;
using soft.square.net.WebApi.Modules.Storage.Web.Items;
using soft.square.net.WebApi.Modules.Storage.Web.Sharing;
using soft.square.net.WebApi.Modules.Storage.Web.Uploads;

namespace soft.square.net.WebApi.Modules.Storage.Web;

/// <summary>
/// The module's route table and its only Carter module (the runtime maps it under <c>/api/storage</c>). Every route
/// needs <c>storage.access</c> or <c>storage.admin</c>, except the signed download links, share links and public folders
/// (anonymous: the signature, the link or the folder is the authorization), group administration (<c>storage.admin</c>)
/// and the event feed (<c>storage.events</c>, for workflow services, or <c>storage.admin</c>).
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
        ShareEndpoints.Map(users);

        var admins = storage.MapGroup("").RequirePermission(StoragePermissions.Admin);
        AccessEndpoints.MapAdmin(admins);
        ItemEndpoints.MapAdmin(admins);

        EventEndpoints.Map(storage.MapGroup("").RequirePermission(StoragePermissions.Events, StoragePermissions.Admin));

        ItemEndpoints.MapLinks(storage);
        ShareEndpoints.MapAnonymous(storage);
    }
}
