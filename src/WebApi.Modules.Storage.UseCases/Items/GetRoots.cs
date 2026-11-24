using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>The navigation pane's starting points: "Files" (the tenant's shared tree) and the caller's home folder.</summary>
public sealed record GetRootsQuery : IQuery<Result<Roots>>;

/// <summary>
/// Creates what is missing on first use: "Files" (everyone may list it, this folder only; admins grant the rest),
/// "Users" (everyone may pass through, nobody may list it) and the caller's home folder (full control for them only).
/// Also puts the caller on Storage's list of people (the Share dialog finds them there).
/// </summary>
public sealed class GetRootsHandler(IStorageStore store, StorageCaller caller, ITenantContext tenant, PeopleBook people) : IQueryHandler<GetRootsQuery, Result<Roots>>
{
    public const string FilesName = "Files";
    public const string HomesName = "Users";

    public async ValueTask<Result<Roots>> Handle(GetRootsQuery query, CancellationToken ct)
    {
        var me = caller.UserId;
        var tenantId = tenant.Required.Id;
        var files = await store.EnsureAsync(c => store.TopFolderAsync(FolderRole.Root, c),
            () => Item.CreateTop(tenantId, FolderRole.Root, FilesName,
                [AccessEntry.Create(PrincipalKind.Everyone, null, null, AceType.Allow, StorageRights.ReadAndExecute)], caller.Now), ct);
        var homes = await store.EnsureAsync(c => store.TopFolderAsync(FolderRole.Homes, c),
            () => Item.CreateTop(tenantId, FolderRole.Homes, HomesName,
                [AccessEntry.Create(PrincipalKind.Everyone, null, null, AceType.Allow, StorageRights.Traverse | StorageRights.ReadAttributes)], caller.Now), ct);
        var home = await store.EnsureAsync(c => store.HomeAsync(homes.Id, me, c), () => Item.CreateHome(homes, me, caller.Now), ct);

        if (await people.RememberCallerAsync(ct)) await store.SaveAsync(ct);

        var subfolders = await store.WithSubfoldersAsync([files.Id, home.Id], ct);
        return new Roots(
            ItemDto.From(files, await caller.RightsAsync([files], ct), subfolders.Contains(files.Id)),
            ItemDto.From(home, await caller.RightsAsync([homes, home], ct), subfolders.Contains(home.Id)),
            tenant.Required.Id);
    }
}
