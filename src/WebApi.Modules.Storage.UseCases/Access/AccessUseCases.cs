using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Access;

/// <summary>
/// One row of the Advanced Security dialog. <see cref="Rights"/> and <see cref="Inheritance"/> are the StorageRights and
/// AceInheritance flags (NTFS values). Inherited rows name the folder they come from.
/// </summary>
public sealed record AceDto(
    PrincipalKind PrincipalKind,
    string? PrincipalId,
    string? PrincipalScope,
    AceType Type,
    int Rights,
    int Inheritance,
    bool Inherited,
    Guid? InheritedFromId,
    string? InheritedFromName)
{
    public static AceDto Explicit(AccessEntry e) =>
        new(e.PrincipalKind, e.PrincipalId, e.PrincipalScope, e.Type, (int)e.Rights, (int)e.Inheritance, false, null, null);

    public static AceDto From(EffectiveEntry e) =>
        new(e.Entry.PrincipalKind, e.Entry.PrincipalId, e.Entry.PrincipalScope, e.Entry.Type, (int)e.Entry.Rights, (int)e.Entry.Inheritance,
            true, e.Source.Id, e.Source.Name);

    public AccessEntry ToEntry() =>
        AccessEntry.Create(PrincipalKind, PrincipalId, PrincipalScope, Type, (StorageRights)Rights, (AceInheritance)Inheritance);
}

/// <summary>An item's security: owner, whether it inherits, its own entries, then the inherited ones (nearest folder first).</summary>
public sealed record AclDto(Guid ItemId, ItemKind Kind, string OwnerId, bool InheritsAccess, int Version, IReadOnlyList<AceDto> Entries);

public sealed record GetAclQuery(Guid Id) : IQuery<Result<AclDto>>;

public sealed class GetAclHandler(ItemAccess access) : IQueryHandler<GetAclQuery, Result<AclDto>>
{
    public async ValueTask<Result<AclDto>> Handle(GetAclQuery query, CancellationToken ct)
    {
        var reached = await access.ReachAsync(query.Id, StorageRights.ReadPermissions, ct);
        return reached.IsSuccess ? Acl.Of(reached.Value.Chain) : reached.As<AclDto>();
    }
}

/// <summary>
/// The Security dialog's Apply (Change permissions): the item's own entries and whether it inherits. Turning inheritance
/// off with <see cref="ConvertInherited"/> keeps the inherited entries as explicit ones (Windows' "Convert inherited
/// permissions into explicit permissions"); without it they are removed. <see cref="Version"/> guards against a lost
/// update.
/// </summary>
public sealed record SetAclCommand(Guid Id, bool InheritsAccess, IReadOnlyList<AceDto> Entries, bool ConvertInherited, int Version) : ICommand<Result<AclDto>>;

public sealed class SetAclHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<SetAclCommand, Result<AclDto>>
{
    public async ValueTask<Result<AclDto>> Handle(SetAclCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.ChangePermissions, ct);
        if (!reached.IsSuccess) return reached.As<AclDto>();
        var item = reached.Value.Item;
        if (command.Version != item.Version) return Result<AclDto>.Conflict("Someone else changed this item first; refresh and try again.");

        var entries = command.Entries.Where(e => !e.Inherited).Select(e => e.ToEntry()).ToList();
        if (!command.InheritsAccess && item.InheritsAccess && command.ConvertInherited)
            entries.AddRange(AccessEvaluator.InheritedAsExplicit(reached.Value.Chain));
        item.SetAccess(command.InheritsAccess, entries, caller.UserId, caller.Now);
        await store.SaveAsync(ct);
        return Acl.Of(reached.Value.Chain);
    }
}

/// <summary>
/// Change the owner. Taking ownership yourself needs Take ownership; giving it to someone else is for Storage admins.
/// </summary>
public sealed record SetOwnerCommand(Guid Id, string? OwnerId) : ICommand<Result<AclDto>>;

public sealed class SetOwnerHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<SetOwnerCommand, Result<AclDto>>
{
    public async ValueTask<Result<AclDto>> Handle(SetOwnerCommand command, CancellationToken ct)
    {
        var owner = string.IsNullOrWhiteSpace(command.OwnerId) ? caller.UserId : command.OwnerId.Trim();
        var self = owner == caller.UserId;
        var reached = await access.ReachAsync(command.Id, self ? StorageRights.TakeOwnership : StorageRights.None, ct);
        if (!reached.IsSuccess) return reached.As<AclDto>();
        if (!self && !await caller.IsAdminAsync(ct)) return Result<AclDto>.Forbidden("Only Storage administrators can give ownership to someone else.");
        reached.Value.Item.SetOwner(owner, caller.UserId, caller.Now);
        await store.SaveAsync(ct);
        return Acl.Of(reached.Value.Chain);
    }
}

/// <summary>The Effective Access tab: what a member may do with the item, through all their entries, groups and grants.</summary>
public sealed record EffectiveAccessDto(string UserId, int Rights, bool IsAdmin, bool CanUseStorage);

public sealed record GetEffectiveAccessQuery(Guid Id, string UserId) : IQuery<Result<EffectiveAccessDto>>;

public sealed class GetEffectiveAccessHandler(ItemAccess access, StorageCaller caller, IAccessScopes scopes)
    : IQueryHandler<GetEffectiveAccessQuery, Result<EffectiveAccessDto>>
{
    public async ValueTask<Result<EffectiveAccessDto>> Handle(GetEffectiveAccessQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.UserId)) return Result<EffectiveAccessDto>.Invalid(new ValidationError("Name the user."));
        var reached = await access.ReachAsync(query.Id, StorageRights.ReadPermissions, ct);
        if (!reached.IsSuccess) return reached.As<EffectiveAccessDto>();

        var userId = query.UserId.Trim();
        var tenantWide = new[] { AccessScope.Tenant };
        var isAdmin = await scopes.UserHasPermissionAsync(userId, StoragePermissions.Admin, tenantWide, ct);
        var canUse = isAdmin || await scopes.UserHasPermissionAsync(userId, StoragePermissions.Access, tenantWide, ct);
        StorageRights rights;
        if (isAdmin) rights = StorageRights.FullControl;
        else
        {
            var matcher = await caller.MatcherForAsync(userId, ct);
            await matcher.PrepareAsync(reached.Value.Chain, ct);
            rights = AccessEvaluator.Evaluate(reached.Value.Chain, matcher);
        }
        return new EffectiveAccessDto(userId, (int)rights, isAdmin, canUse);
    }
}

internal static class Acl
{
    public static AclDto Of(IReadOnlyList<Item> chain)
    {
        var item = chain[^1];
        var rows = item.Entries.Where(e => e.Type == AceType.Deny).Concat(item.Entries.Where(e => e.Type == AceType.Allow))
            .Select(AceDto.Explicit)
            .Concat(AccessEvaluator.Inherited(chain).Select(AceDto.From))
            .ToArray();
        return new AclDto(item.Id, item.Kind, item.OwnerId, item.InheritsAccess, item.Version, rows);
    }
}
