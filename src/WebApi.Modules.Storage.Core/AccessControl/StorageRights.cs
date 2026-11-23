namespace soft.square.net.WebApi.Modules.Storage.Core.AccessControl;

/// <summary>
/// The rights of an access entry: the NTFS file-system rights with their NTFS values (a folder's ListFolder is a file's
/// ReadData, CreateFiles is WriteData, CreateFolders is AppendData, Traverse is ExecuteFile), plus Storage's own
/// <see cref="Share"/>. The composite values are the presets of the Windows Security dialog.
/// </summary>
[Flags]
public enum StorageRights
{
    None = 0,
    /// <summary>Folders: list the contents. Files: read (download, preview) the data.</summary>
    ListFolder = 0x1,
    /// <summary>Folders: create files in it. Files: write (replace with a new version).</summary>
    CreateFiles = 0x2,
    /// <summary>Folders: create subfolders. Files: append data.</summary>
    CreateFolders = 0x4,
    ReadExtendedAttributes = 0x8,
    WriteExtendedAttributes = 0x10,
    /// <summary>Folders: pass through to reach items below. Files: execute (open in an app).</summary>
    Traverse = 0x20,
    /// <summary>Delete the folder's children, whatever their own rights say.</summary>
    DeleteChildren = 0x40,
    ReadAttributes = 0x80,
    WriteAttributes = 0x100,
    Delete = 0x10000,
    ReadPermissions = 0x20000,
    ChangePermissions = 0x40000,
    TakeOwnership = 0x80000,
    /// <summary>Not NTFS: create share links and share with people.</summary>
    Share = 0x1000000,

    Read = ListFolder | ReadAttributes | ReadExtendedAttributes | ReadPermissions,
    Write = CreateFiles | CreateFolders | WriteAttributes | WriteExtendedAttributes,
    ReadAndExecute = Read | Traverse,
    Modify = ReadAndExecute | Write | Delete,
    FullControl = Modify | DeleteChildren | ChangePermissions | TakeOwnership | Share
}

/// <summary>
/// Where an entry on a folder applies, with the NTFS flags: <see cref="Subfolders"/> (container inherit) and
/// <see cref="Files"/> (object inherit) carry it down, <see cref="NoPropagate"/> only to the folder's direct children,
/// <see cref="InheritOnly"/> not on the folder itself. "This folder, subfolders and files" is Subfolders | Files.
/// Entries on files use <see cref="None"/>.
/// </summary>
[Flags]
public enum AceInheritance
{
    None = 0,
    Subfolders = 1,
    Files = 2,
    NoPropagate = 4,
    InheritOnly = 8
}

[JsonConverter(typeof(JsonStringEnumConverter<AceType>))]
public enum AceType
{
    Allow,
    Deny
}

/// <summary>Who an access entry is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrincipalKind>))]
public enum PrincipalKind
{
    /// <summary>One member of the tenant (<see cref="AccessEntry.PrincipalId"/> is the token's <c>sub</c>).</summary>
    User,
    /// <summary>Every member of the tenant who may use Storage.</summary>
    Everyone,
    /// <summary>
    /// Whoever holds a platform permission in a scope right now (<see cref="AccessEntry.PrincipalId"/> is the
    /// permission, <see cref="AccessEntry.PrincipalScope"/> the scope, e.g. content.editor in press:daily), so shift
    /// grants give access only while the shift runs.
    /// </summary>
    Permission,
    /// <summary>A Storage group (<see cref="AccessEntry.PrincipalId"/> is its id).</summary>
    Group,
    /// <summary>NTFS CREATOR OWNER: on a folder, inherited by each new item as an entry for that item's owner.</summary>
    CreatorOwner
}
