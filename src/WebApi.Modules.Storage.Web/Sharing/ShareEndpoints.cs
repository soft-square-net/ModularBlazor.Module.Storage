using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;
using soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;

namespace soft.square.net.WebApi.Modules.Storage.Web.Sharing;

public sealed record ShareWithPeopleRequest(IReadOnlyList<string> UserIds, ShareAccess Access);

public sealed record CreateShareLinkRequest(DateTimeOffset? ExpiresAt, string? Password, int? MaxDownloads);

public sealed record SetPublicRequest(bool Public);

/// <summary>Sharing: with people (access entries), "anyone with the link" links, public folders.</summary>
public static class ShareEndpoints
{
    /// <summary>The header a share link's password travels in (never the URL).</summary>
    public const string PasswordHeader = "X-Share-Password";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/people", async (string? search, ISender sender, CancellationToken ct) =>
                (await sender.Send(new SearchPeopleQuery(search), ct)).ToOkResult())
            .WithName("Storage_SearchPeople")
            .WithSummary("Tenant members who have used Storage, whose name or email contains the text (2 characters or more).");

        app.MapGet("/shared-with-me", async (ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetSharedWithMeQuery(), ct)).ToOkResult())
            .WithName("Storage_GetSharedWithMe")
            .WithSummary("Items shared with you by name that you can still open, with their owner and path.");

        app.MapGet("/items/{id:guid}/sharing", async (Guid id, ISender sender, CancellationToken ct) =>
                (await sender.Send(new GetSharingQuery(id), ct)).ToOkResult())
            .WithName("Storage_GetSharing")
            .WithSummary("The Share dialog (Share): who the item is shared with, its links, and whether it is public.");

        app.MapPost("/items/{id:guid}/sharing/people", async (Guid id, ShareWithPeopleRequest request, ISender sender, CancellationToken ct) =>
                (await sender.Send(new ShareWithPeopleCommand(id, request.UserIds, request.Access), ct)).ToOkResult())
            .WithName("Storage_ShareWithPeople")
            .WithSummary("Share with people to view or to edit (Share; edit also needs Modify yourself).");

        app.MapDelete("/items/{id:guid}/sharing/people/{userId}", async (Guid id, string userId, ISender sender, CancellationToken ct) =>
                (await sender.Send(new StopSharingCommand(id, userId), ct)).ToOkResult())
            .WithName("Storage_StopSharing")
            .WithSummary("Stop sharing with someone: removes their allow entries on the item (Share).");

        app.MapPut("/items/{id:guid}/sharing/public", async (Guid id, SetPublicRequest request, ISender sender, CancellationToken ct) =>
                (await sender.Send(new SetPublicCommand(id, request.Public), ct)).ToOkResult())
            .WithName("Storage_SetPublic")
            .WithSummary("Make a folder public (anyone can read its files without signing in) or private (Share and Change permissions).");

        app.MapPost("/items/{id:guid}/sharing/links", async (Guid id, CreateShareLinkRequest request, ISender sender, CancellationToken ct) =>
                (await sender.Send(new CreateShareLinkCommand(id, request.ExpiresAt, request.Password, request.MaxDownloads), ct)).ToOkResult())
            .WithName("Storage_CreateShareLink")
            .WithSummary("A new \"anyone with the link\" link: optional expiry, password and download limit (Share).");

        app.MapDelete("/sharing/links/{linkId:guid}", async (Guid linkId, ISender sender, CancellationToken ct) =>
                (await sender.Send(new DeleteShareLinkCommand(linkId), ct)).ToNoContentResult())
            .WithName("Storage_DeleteShareLink")
            .WithSummary("Remove a link (its creator, Share on the item, or a Storage admin); it stops working at once.");
    }

    /// <summary>
    /// The anonymous side: share links and public folders. The URL names the tenant (an &lt;img&gt; or a link sends no
    /// header), and each request runs in a scope of that tenant; a tenant named by the request itself must be the same.
    /// </summary>
    public static void MapAnonymous(IEndpointRouteBuilder app)
    {
        app.MapGet("/shares/{tenant}/{token}", async Task<Results<Ok<SharedView>, ProblemHttpResult>> (string tenant, string token, Guid? folderId,
                [FromHeader(Name = PasswordHeader)] string? password, HttpContext http, CancellationToken ct) =>
                await InTenantAsync(http, tenant, async sender => (await sender.Send(new OpenShareQuery(token, password, folderId), ct)).ToOkResult()))
            .AllowAnonymous()
            .TenantOptional()
            .WithName("Storage_OpenShare")
            .WithSummary("Open a share link (anonymous): the shared file, or a folder of the shared tree. 401 when it needs a password (header X-Share-Password).");

        app.MapGet("/shares/{tenant}/{token}/files/{fileId:guid}/link", async Task<Results<Ok<DownloadLink>, ProblemHttpResult>> (string tenant, string token,
                Guid fileId, bool? inline, [FromHeader(Name = PasswordHeader)] string? password, HttpContext http, CancellationToken ct) =>
                await InTenantAsync(http, tenant, async sender =>
                    (await sender.Send(new OpenSharedFileQuery(token, password, fileId, inline ?? false), ct)).ToOkResult()))
            .AllowAnonymous()
            .TenantOptional()
            .WithName("Storage_OpenSharedFile")
            .WithSummary("A short-lived URL for a file the link shares (anonymous); counts as a download.");

        app.MapGet("/public/{tenant}/{fileId:guid}/{**name}", async Task<Results<RedirectHttpResult, ProblemHttpResult>> (string tenant, Guid fileId,
                HttpContext http, CancellationToken ct) =>
                await InTenantAsync<RedirectHttpResult>(http, tenant, async Task<Results<RedirectHttpResult, ProblemHttpResult>> (sender) =>
                {
                    var link = await sender.Send(new OpenPublicFileQuery(fileId), ct);
                    if (!link.IsSuccess) return link.ToProblem();
                    // Shorter than the link's lifetime, so a cached redirect never points at an expired link.
                    http.Response.Headers.CacheControl = "public, max-age=60";
                    return TypedResults.Redirect(link.Value.Url.ToString());
                }))
            .AllowAnonymous()
            .TenantOptional()
            .ExcludeFromDescription()
            .WithName("Storage_OpenPublicFile");
    }

    private static async Task<Results<T, ProblemHttpResult>> InTenantAsync<T>(HttpContext http, string tenantId,
        Func<ISender, Task<Results<T, ProblemHttpResult>>> run) where T : IResult
    {
        var services = http.RequestServices;
        var info = services.GetRequiredService<ITenantDirectory>().Find(tenantId);
        var requested = services.GetRequiredService<ITenantContext>().TenantId;
        if (info is not { IsActive: true } || requested is not null && !string.Equals(requested, info.Id, StringComparison.OrdinalIgnoreCase))
            return TypedResults.Problem("Not found.", statusCode: StatusCodes.Status404NotFound);
        await using var scope = services.CreateTenantScope(info);
        return await run(scope.ServiceProvider.GetRequiredService<ISender>());
    }
}
