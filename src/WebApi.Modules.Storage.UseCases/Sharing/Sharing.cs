using System.Text.Json.Serialization;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;

/// <summary>What the Share dialog offers: view (read, and pass through folders) or edit (modify).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShareAccess>))]
public enum ShareAccess
{
    View,
    Edit,
    /// <summary>Set in the Security dialog to something else; shown, and replaced if changed here.</summary>
    Custom
}

/// <summary>A person the item is shared with: an explicit allow entry for one user on the item itself.</summary>
public sealed record SharedPersonDto(string UserId, string Name, string? Email, ShareAccess Access);

/// <summary>A share link as its creator and the item's sharers see it (the token makes the URL).</summary>
public sealed record ShareLinkDto(Guid Id, string Token, Guid ItemId, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    bool HasPassword, int? MaxDownloads, int Downloads, bool Active)
{
    public static ShareLinkDto From(ShareLink l, DateTimeOffset now) =>
        new(l.Id, l.Token, l.ItemId, l.CreatedBy, l.CreatedAt, l.ExpiresAt, l.HasPassword, l.MaxDownloads, l.Downloads, l.IsActive(now));
}

/// <summary>
/// The Share dialog: who the item is shared with, its links, and whether it is public (a folder marked public, or a file
/// under one: <see cref="PublicFolderId"/>). <see cref="TenantId"/> goes into the public and link URLs.
/// </summary>
public sealed record SharingDto(Guid ItemId, ItemKind Kind, string Name, string TenantId, int Version, bool CanEdit, bool CanChangePublic,
    bool IsPublic, Guid? PublicFolderId, IReadOnlyList<SharedPersonDto> People, IReadOnlyList<ShareLinkDto> Links);

public sealed record GetSharingQuery(Guid Id) : IQuery<Result<SharingDto>>;

public sealed class GetSharingHandler(ItemAccess access, Sharer sharer) : IQueryHandler<GetSharingQuery, Result<SharingDto>>
{
    public async ValueTask<Result<SharingDto>> Handle(GetSharingQuery query, CancellationToken ct)
    {
        var reached = await access.ReachAsync(query.Id, StorageRights.Share, ct);
        return reached.IsSuccess ? await sharer.ViewAsync(reached.Value, ct) : reached.As<SharingDto>();
    }
}

/// <summary>
/// Share with people (Share on the item): gives each a view or edit entry on the item (for a folder, on everything below
/// it), replacing what the dialog gave them before. Giving edit needs Modify yourself: you cannot share more than you have.
/// </summary>
public sealed record ShareWithPeopleCommand(Guid Id, IReadOnlyList<string> UserIds, ShareAccess Access) : ICommand<Result<SharingDto>>;

public sealed class ShareWithPeopleHandler(IStorageStore store, ItemAccess access, StorageCaller caller, Sharer sharer)
    : ICommandHandler<ShareWithPeopleCommand, Result<SharingDto>>
{
    public async ValueTask<Result<SharingDto>> Handle(ShareWithPeopleCommand command, CancellationToken ct)
    {
        var ids = command.UserIds.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return Result<SharingDto>.Invalid(new ValidationError("Pick at least one person."));
        if (ids.Length > 50) return Result<SharingDto>.Invalid(new ValidationError("Share with at most 50 people at a time."));
        if (command.Access is not (ShareAccess.View or ShareAccess.Edit)) return Result<SharingDto>.Invalid(new ValidationError("Share to view or to edit."));
        var reached = await access.ReachAsync(command.Id, StorageRights.Share, ct);
        if (!reached.IsSuccess) return reached.As<SharingDto>();
        if (command.Access == ShareAccess.Edit && !reached.Value.Has(StorageRights.Modify))
            return Result<SharingDto>.Forbidden("You can only share to view: you cannot edit this item yourself.");

        var item = reached.Value.Item;
        var rights = command.Access == ShareAccess.Edit ? StorageRights.Modify : StorageRights.ReadAndExecute;
        var inheritance = item.IsFolder ? AceInheritance.Subfolders | AceInheritance.Files : AceInheritance.None;
        var entries = item.Entries.Where(e => !Sharer.IsPersonalAllow(e) || !ids.Contains(e.PrincipalId, StringComparer.Ordinal))
            .Concat(ids.Select(id => AccessEntry.Create(PrincipalKind.User, id, null, AceType.Allow, rights, inheritance)))
            .ToList();
        item.SetAccess(item.InheritsAccess, entries, caller.UserId, caller.Now);
        await store.SaveAsync(ct);
        return await sharer.ViewAsync(reached.Value, ct);
    }
}

/// <summary>Stop sharing with someone (Share on the item): removes their allow entries on the item itself.</summary>
public sealed record StopSharingCommand(Guid Id, string UserId) : ICommand<Result<SharingDto>>;

public sealed class StopSharingHandler(IStorageStore store, ItemAccess access, StorageCaller caller, Sharer sharer)
    : ICommandHandler<StopSharingCommand, Result<SharingDto>>
{
    public async ValueTask<Result<SharingDto>> Handle(StopSharingCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.Share, ct);
        if (!reached.IsSuccess) return reached.As<SharingDto>();
        var item = reached.Value.Item;
        var userId = command.UserId?.Trim();
        if (item.Entries.Any(e => Sharer.IsPersonalAllow(e) && e.PrincipalId == userId))
        {
            item.SetAccess(item.InheritsAccess, item.Entries.Where(e => !(Sharer.IsPersonalAllow(e) && e.PrincipalId == userId)).ToList(),
                caller.UserId, caller.Now);
            await store.SaveAsync(ct);
        }
        return await sharer.ViewAsync(reached.Value, ct);
    }
}

/// <summary>
/// Make a folder public, or private again: its files can be read by anyone at /public/{tenant}/{id}/{name}, without
/// signing in (published media). Needs Share and Change permissions on the folder.
/// </summary>
public sealed record SetPublicCommand(Guid Id, bool Public) : ICommand<Result<SharingDto>>;

public sealed class SetPublicHandler(IStorageStore store, ItemAccess access, StorageCaller caller, Sharer sharer)
    : ICommandHandler<SetPublicCommand, Result<SharingDto>>
{
    public async ValueTask<Result<SharingDto>> Handle(SetPublicCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.Share | StorageRights.ChangePermissions, ct);
        if (!reached.IsSuccess) return reached.As<SharingDto>();
        reached.Value.Item.SetPublic(command.Public, caller.UserId, caller.Now);
        await store.SaveAsync(ct);
        return await sharer.ViewAsync(reached.Value, ct);
    }
}

/// <summary>"Shared with me": items someone gave the caller access to by name, which the caller may still open.</summary>
public sealed record GetSharedWithMeQuery : IQuery<Result<IReadOnlyList<SharedItemDto>>>;

/// <summary>An item shared with the caller, with who owns it and where it is.</summary>
public sealed record SharedItemDto(ItemDto Item, string OwnerName, IReadOnlyList<Crumb> Path);

public sealed class GetSharedWithMeHandler(IStorageStore store, StorageCaller caller, PeopleBook people)
    : IQueryHandler<GetSharedWithMeQuery, Result<IReadOnlyList<SharedItemDto>>>
{
    public async ValueTask<Result<IReadOnlyList<SharedItemDto>>> Handle(GetSharedWithMeQuery query, CancellationToken ct)
    {
        var me = caller.UserId;
        var found = new List<(Item Item, IReadOnlyList<Item> Chain, StorageRights Rights)>();
        foreach (var item in await store.SharedWithAsync(me, 500, ct))
        {
            if (item.OwnerId == me) continue; // your own items (your home folder names you too)
            var chain = await store.ChainAsync(item.Id, ct);
            if (chain is null || chain.Any(i => i.IsDeleted)) continue;
            var rights = await caller.RightsAsync(chain, ct);
            if (!rights.HasFlag(StorageRights.ListFolder)) continue; // a deny took it away again
            found.Add((item, chain, rights));
        }
        var owners = await people.ResolveAsync(found.Select(f => f.Item.OwnerId).ToArray(), ct);
        var subfolders = await store.WithSubfoldersAsync(found.Where(f => f.Item.IsFolder).Select(f => f.Item.Id).ToArray(), ct);
        return found.Select(f => new SharedItemDto(ItemDto.From(f.Item, f.Rights, subfolders.Contains(f.Item.Id)), owners[f.Item.OwnerId].Name,
            ItemViews.Path(f.Chain))).ToArray();
    }
}

/// <summary>Builds the Share dialog's view of an item the caller reached with Share.</summary>
public sealed class Sharer(IStorageStore store, StorageCaller caller, PeopleBook people, ITenantContext tenant)
{
    /// <summary>An entry the Share dialog manages: allow, for one user.</summary>
    public static bool IsPersonalAllow(AccessEntry e) => e.PrincipalKind == PrincipalKind.User && e.Type == AceType.Allow;

    public async Task<SharingDto> ViewAsync(Reached reached, CancellationToken ct)
    {
        var item = reached.Item;
        var personal = item.Entries.Where(IsPersonalAllow).GroupBy(e => e.PrincipalId!, StringComparer.Ordinal)
            .Where(g => g.Key != item.OwnerId)
            .ToDictionary(g => g.Key, g => g.Aggregate(StorageRights.None, (r, e) => r | e.Rights), StringComparer.Ordinal);
        var names = await people.ResolveAsync(personal.Keys.ToArray(), ct);
        var shared = personal.Select(p => new SharedPersonDto(p.Key, names[p.Key].Name, names[p.Key].Email, AccessOf(p.Value)))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var links = (await store.LinksOfAsync(item.Id, ct)).Select(l => ShareLinkDto.From(l, caller.Now)).ToArray();
        var publicFolder = reached.Chain.LastOrDefault(i => i.IsPublic);
        return new SharingDto(item.Id, item.Kind, item.Name, tenant.Required.Id, item.Version, reached.Has(StorageRights.Modify),
            item.IsFolder && item.Role != FolderRole.Homes && reached.Has(StorageRights.ChangePermissions),
            item.IsPublic, publicFolder?.Id, shared, links);
    }

    private static ShareAccess AccessOf(StorageRights rights) =>
        (rights & StorageRights.Modify) == StorageRights.Modify ? ShareAccess.Edit
        : (rights & StorageRights.ReadAndExecute) == StorageRights.ReadAndExecute ? ShareAccess.View
        : ShareAccess.Custom;
}
