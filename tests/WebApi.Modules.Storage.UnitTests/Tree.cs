namespace soft.square.net.WebApi.Modules.Storage.UnitTests;

/// <summary>Builds small trees of items for the tests: Files / a / b / file.txt.</summary>
public static class Tree
{
    public static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    public static Item Root(params AccessEntry[] entries) => Item.CreateTop("acme", FolderRole.Root, "Files", entries, Now);

    public static Item Folder(Item parent, string name, string by = "owner-1") => Item.CreateFolder(parent, name, by, Now);

    public static Item File(Item parent, string name, string by = "owner-1") =>
        Item.CreateFile(parent, name, by, FileVersion.Create($"t/acme/2026/11/{Guid.NewGuid():N}", 10, "text/plain", by, Now), Now);

    public static AccessEntry Allow(PrincipalKind kind, string? id, StorageRights rights, AceInheritance inheritance = AceInheritance.Subfolders | AceInheritance.Files, string? scope = null) =>
        AccessEntry.Create(kind, id, scope, AceType.Allow, rights, inheritance);

    public static AccessEntry Deny(PrincipalKind kind, string? id, StorageRights rights, AceInheritance inheritance = AceInheritance.Subfolders | AceInheritance.Files) =>
        AccessEntry.Create(kind, id, null, AceType.Deny, rights, inheritance);

    public static void Set(Item item, bool inherits, params AccessEntry[] entries) => item.SetAccess(inherits, entries, "admin", Now);
}

/// <summary>A principal for the evaluator: a user, their groups and the (permission, scope) pairs they hold.</summary>
public sealed class TestPrincipal(string userId, string[]? groups = null, (string Permission, string Scope)[]? permissions = null) : IPrincipalMatcher
{
    public string UserId { get; } = userId;

    public bool Matches(AccessEntry entry) => entry.PrincipalKind switch
    {
        PrincipalKind.User => entry.PrincipalId == UserId,
        PrincipalKind.Everyone => true,
        PrincipalKind.Group => groups?.Contains(entry.PrincipalId) == true,
        PrincipalKind.Permission => permissions?.Contains((entry.PrincipalId!, entry.PrincipalScope!)) == true,
        _ => false
    };
}
