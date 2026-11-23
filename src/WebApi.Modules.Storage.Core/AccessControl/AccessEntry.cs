namespace soft.square.net.WebApi.Modules.Storage.Core.AccessControl;

/// <summary>An access control entry (ACE) of a folder or file: allow or deny rights to a principal.</summary>
public sealed class AccessEntry
{
    private AccessEntry() { } // EF Core

    public const int PrincipalIdLength = 128;
    public const int PrincipalScopeLength = 200;

    public PrincipalKind PrincipalKind { get; private set; }
    public string? PrincipalId { get; private set; }
    public string? PrincipalScope { get; private set; }
    public AceType Type { get; private set; }
    public StorageRights Rights { get; private set; }
    public AceInheritance Inheritance { get; private set; }

    public static AccessEntry Create(PrincipalKind kind, string? principalId, string? scope, AceType type, StorageRights rights,
        AceInheritance inheritance = AceInheritance.None)
    {
        if (!Enum.IsDefined(kind)) throw new DomainException("Unknown principal kind.");
        principalId = string.IsNullOrWhiteSpace(principalId) ? null : principalId.Trim();
        scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
        switch (kind)
        {
            case PrincipalKind.User when principalId is null:
                throw new DomainException("A user entry needs the user's id.");
            case PrincipalKind.Group when !Guid.TryParse(principalId, out _):
                throw new DomainException("A group entry needs the group's id.");
            case PrincipalKind.Permission when principalId is null || scope is null:
                throw new DomainException("A permission entry needs the permission and its scope (\"tenant\" for the whole tenant).");
            case PrincipalKind.Everyone or PrincipalKind.CreatorOwner:
                principalId = null;
                scope = null;
                break;
            case PrincipalKind.User or PrincipalKind.Group:
                scope = null;
                break;
        }
        if (kind == PrincipalKind.Group) principalId = Guid.Parse(principalId!).ToString("D");
        if (principalId?.Length > PrincipalIdLength || scope?.Length > PrincipalScopeLength)
            throw new DomainException("The entry's principal is too long.");
        if (!Enum.IsDefined(type)) throw new DomainException("An entry allows or denies.");
        rights &= StorageRights.FullControl;
        if (rights == StorageRights.None) throw new DomainException("An entry needs at least one right.");
        inheritance &= AceInheritance.Subfolders | AceInheritance.Files | AceInheritance.NoPropagate | AceInheritance.InheritOnly;
        if ((inheritance & (AceInheritance.Subfolders | AceInheritance.Files)) == 0)
            inheritance = AceInheritance.None; // NoPropagate / InheritOnly mean nothing without a target
        return new AccessEntry
        {
            PrincipalKind = kind, PrincipalId = principalId, PrincipalScope = scope, Type = type, Rights = rights, Inheritance = inheritance
        };
    }

    /// <summary>Applies to the item it is set on (not inherit-only).</summary>
    public bool AppliesToSelf => !Inheritance.HasFlag(AceInheritance.InheritOnly);

    /// <summary>Applied to an item <paramref name="distance"/> levels below the folder it is set on (1 = a direct child).</summary>
    public bool InheritedBy(bool targetIsFolder, int distance)
    {
        if (distance > 1 && Inheritance.HasFlag(AceInheritance.NoPropagate)) return false;
        return Inheritance.HasFlag(targetIsFolder ? AceInheritance.Subfolders : AceInheritance.Files);
    }

    /// <summary>The same principal, type and rights with other inheritance flags (converting inherited entries).</summary>
    public AccessEntry WithInheritance(AceInheritance inheritance) =>
        Create(PrincipalKind, PrincipalId, PrincipalScope, Type, Rights, inheritance);
}
