using soft.square.net.WebApi.Modules.Storage.Core.AccessControl;
using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;

namespace soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;

[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    Folder,
    File
}

/// <summary>Folders the module creates itself; they cannot be renamed, moved or deleted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FolderRole>))]
public enum FolderRole
{
    Normal,
    /// <summary>The tenant's shared files ("Files"), the top of the shared tree.</summary>
    Root,
    /// <summary>"Users": holds the home folders. Everyone may pass through it, nobody lists it.</summary>
    Homes,
    /// <summary>A user's home folder, owned by them, not inheriting from "Users".</summary>
    Home
}

/// <summary>
/// A file's virus scan state (of its current version's bytes). Without a scanner every file is <see cref="Clean"/>;
/// with one, a file that is not clean cannot be downloaded, previewed, opened by a share link or served publicly.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScanStatus>))]
public enum ScanStatus
{
    Clean,
    Pending,
    Infected
}

/// <summary>
/// A folder or a file of the tenant: the database is the source of truth, the bytes are blobs in the storage provider
/// under keys that never change, so rename and move only touch rows.
/// <list type="bullet">
/// <item><see cref="Path"/> is materialized: the ids from the top folder down to this item, <c>/{id}/{id}/</c> (32 hex
/// digits each). It gives the item's chain of folders for access checks in one query, and moving a folder rewrites the
/// paths of everything below it.</item>
/// <item>Access: an NTFS-style list of <see cref="Entries"/>, inherited from the folders above unless
/// <see cref="InheritsAccess"/> is off (see <see cref="AccessEvaluator"/>), and an <see cref="OwnerId"/>.</item>
/// <item>Deleting moves the item to the recycle bin (<see cref="DeletedAt"/>); its name is free again in its folder.</item>
/// <item>What happens to it is raised as an <see cref="ItemEvent"/> (the outbox feed for workflows); system folders raise none.</item>
/// </list>
/// </summary>
public sealed class Item : AggregateRoot<Guid>, ITenantOwned
{
    /// <summary>Folder levels below a top folder; keeps <see cref="Path"/> within 2000 characters.</summary>
    public const int MaxDepth = 60;
    public const int PathLength = 2000;
    public const int UserIdLength = 128;
    public const int ContentTypeLength = 200;

    private readonly List<AccessEntry> _entries = [];
    private readonly List<FileVersion> _versions = [];

    private Item() { } // EF Core

    public string TenantId { get; set; } = "";
    public ItemKind Kind { get; private set; }
    public FolderRole Role { get; private set; }
    public Guid? ParentId { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>The name without case, unique in the folder among items that are not deleted.</summary>
    public string NameKey { get; private set; } = "";
    public string Path { get; private set; } = "";
    /// <summary>0 for a top folder.</summary>
    public int Depth { get; private set; }

    public string OwnerId { get; private set; } = "";
    public bool InheritsAccess { get; private set; } = true;
    /// <summary>Folders: anyone may read the files below it, without signing in (published media), at /public/{tenant}/{id}/{name}.</summary>
    public bool IsPublic { get; private set; }
    /// <summary>The item's own (explicit) entries; inherited ones are computed from the folders above.</summary>
    public IReadOnlyList<AccessEntry> Entries => _entries;

    /// <summary>Files: the current version's size and type.</summary>
    public long Size { get; private set; }
    public string? ContentType { get; private set; }
    public int CurrentVersion { get; private set; }
    public IReadOnlyList<FileVersion> Versions => _versions;
    public ScanStatus Scan { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset ModifiedAt { get; private set; }
    public string ModifiedBy { get; private set; } = "";
    public DateTimeOffset? DeletedAt { get; private set; }
    public string? DeletedBy { get; private set; }

    /// <summary>Concurrency token: raised by every change.</summary>
    public int Version { get; private set; }

    public bool IsFolder => Kind == ItemKind.Folder;
    public bool IsDeleted => DeletedAt is not null;
    public bool IsSystem => Role != FolderRole.Normal;
    public string? Extension => IsFolder ? null : ItemNames.Extension(Name);
    public FileVersion? Current => _versions.FirstOrDefault(v => v.Number == CurrentVersion);

    /// <summary>The ids of the folders above this item, from the top down.</summary>
    public IReadOnlyList<Guid> AncestorIds() =>
        Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Where(id => id != Id).ToArray();

    /// <summary>
    /// A top folder (<see cref="FolderRole.Root"/> or <see cref="FolderRole.Homes"/>) with its default entries. Its id
    /// comes from the tenant and the role (<see cref="TopId"/>), so two replicas creating it at once collide on the key
    /// instead of making two.
    /// </summary>
    public static Item CreateTop(string tenantId, FolderRole role, string name, IEnumerable<AccessEntry> entries, DateTimeOffset now)
    {
        if (role is not (FolderRole.Root or FolderRole.Homes)) throw new ArgumentOutOfRangeException(nameof(role));
        var item = New(ItemKind.Folder, null, name, "system", now, TopId(tenantId, role));
        item.Role = role;
        item.InheritsAccess = false;
        item._entries.AddRange(entries);
        return item;
    }

    /// <summary>The id of a tenant's top folder: a name-based (MD5) UUID of "storage:{tenant}:{role}".</summary>
    public static Guid TopId(string tenantId, FolderRole role)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"storage:{tenantId}:{role}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }

    /// <summary>A user's home folder under "Users": named by their id, owned by them, full control, not inheriting.</summary>
    public static Item CreateHome(Item homes, string userId, DateTimeOffset now)
    {
        if (homes.Role != FolderRole.Homes) throw new DomainException("Home folders live in the Users folder.");
        var item = New(ItemKind.Folder, homes, userId, userId, now);
        item.Role = FolderRole.Home;
        item.InheritsAccess = false;
        item._entries.Add(AccessEntry.Create(PrincipalKind.User, userId, null, AceType.Allow, StorageRights.FullControl,
            AceInheritance.Subfolders | AceInheritance.Files));
        return item;
    }

    public static Item CreateFolder(Item parent, string name, string by, DateTimeOffset now)
    {
        var item = New(ItemKind.Folder, parent, name, by, now);
        item.Happened(StorageEventTypes.Created, by, now);
        return item;
    }

    /// <param name="scan">The scan state of <paramref name="first"/>'s bytes (Pending for a new upload when a scanner is configured).</param>
    public static Item CreateFile(Item parent, string name, string by, FileVersion first, DateTimeOffset now, ScanStatus scan = ScanStatus.Clean)
    {
        var item = New(ItemKind.File, parent, name, by, now);
        item.SetVersion(first, scan, by, now);
        item.Happened(StorageEventTypes.Created, by, now);
        return item;
    }

    private static Item New(ItemKind kind, Item? parent, string name, string by, DateTimeOffset now, Guid? id = null)
    {
        if (parent is not null)
        {
            if (!parent.IsFolder) throw new DomainException("Items can only be created in a folder.");
            if (parent.IsDeleted) throw new DomainException("The folder is in the recycle bin.");
            if (parent.Depth + 1 > MaxDepth) throw new DomainException($"Folders can be nested at most {MaxDepth} levels deep.");
        }
        var item = new Item
        {
            Id = id ?? Guid.CreateVersion7(now),
            Kind = kind,
            OwnerId = Require(by),
            CreatedAt = now,
            CreatedBy = by,
            ModifiedAt = now,
            ModifiedBy = by
        };
        item.SetName(name);
        item.Place(parent);
        return item;
    }

    /// <summary>A new version of a file becomes the current one (upload over it, or an editor's save).</summary>
    /// <param name="scan">The scan state of the version's bytes.</param>
    public void AddVersion(FileVersion version, string by, DateTimeOffset now, ScanStatus scan = ScanStatus.Clean)
    {
        SetVersion(version, scan, by, now);
        Happened(StorageEventTypes.Versioned, by, now);
    }

    private void SetVersion(FileVersion version, ScanStatus scan, string by, DateTimeOffset now)
    {
        if (IsFolder) throw new DomainException("Folders have no versions.");
        EnsureNotDeleted();
        var number = _versions.Count == 0 ? 1 : _versions.Max(v => v.Number) + 1;
        _versions.Add(version.Numbered(number));
        CurrentVersion = number;
        Size = version.Size;
        ContentType = version.ContentType;
        Scan = scan;
        Touch(by, now);
    }

    /// <summary>
    /// The scanner's verdict on the bytes of <paramref name="blobKey"/>: applied when they are the current version's
    /// (an older version's verdict stays with its blob). Returns whether it applied.
    /// </summary>
    public bool Scanned(string blobKey, string? threat, DateTimeOffset now)
    {
        if (Current?.BlobKey != blobKey) return false;
        var scan = threat is null ? ScanStatus.Clean : ScanStatus.Infected;
        if (Scan == scan) return false;
        Scan = scan;
        Version++;
        Happened(StorageEventTypes.Scanned, "system", now, threat is null ? "clean" : $"infected: {threat}");
        return true;
    }

    public void Rename(string name, string by, DateTimeOffset now)
    {
        EnsureEditable();
        var old = Name;
        SetName(name);
        Touch(by, now);
        if (old != Name) Happened(StorageEventTypes.Renamed, by, now, old);
    }

    /// <summary>
    /// Moves the item into <paramref name="parent"/>. Returns the old path and depth: everything below a moved folder
    /// must have its path rewritten in the same transaction (the repository does it).
    /// </summary>
    /// <param name="subtreeHeight">Levels below this item (0 for a file or an empty folder), to stay within <see cref="MaxDepth"/>.</param>
    public (string OldPath, int OldDepth) MoveTo(Item parent, int subtreeHeight, string by, DateTimeOffset now)
    {
        EnsureEditable();
        if (!parent.IsFolder) throw new DomainException("Items can only be moved into a folder.");
        if (parent.IsDeleted) throw new DomainException("The folder is in the recycle bin.");
        if (parent.Id == Id || parent.Path.StartsWith(Path, StringComparison.Ordinal))
            throw new DomainException("The destination folder is a subfolder of the source folder.");
        if (parent.Role == FolderRole.Homes) throw new DomainException("Only home folders live in the Users folder.");
        var old = (Path, Depth);
        if (parent.Depth + 1 + subtreeHeight > MaxDepth) throw new DomainException($"Folders can be nested at most {MaxDepth} levels deep.");
        var oldParent = ParentId;
        Place(parent);
        Touch(by, now);
        Happened(StorageEventTypes.Moved, by, now, oldParent?.ToString("D"));
        return old;
    }

    /// <summary>
    /// Rewrites the path of an item below a moved folder: <paramref name="oldPrefix"/> (the folder's old path) becomes
    /// <paramref name="newPrefix"/>. Only the repository calls it, in the same save as the move.
    /// </summary>
    public void Rebase(string oldPrefix, string newPrefix, int depthDelta)
    {
        if (!Path.StartsWith(oldPrefix, StringComparison.Ordinal)) throw new InvalidOperationException($"'{Name}' is not below '{oldPrefix}'.");
        Path = newPrefix + Path[oldPrefix.Length..];
        Depth += depthDelta;
    }

    /// <summary>Moves the item to the recycle bin; it keeps its folder, so it can be restored there.</summary>
    public void Delete(string by, DateTimeOffset now)
    {
        EnsureEditable();
        DeletedAt = now;
        DeletedBy = by;
        NameKey = ItemNames.DeletedKey(Id);
        Version++;
        Happened(StorageEventTypes.Deleted, by, now);
    }

    /// <summary>
    /// Takes the item out of the recycle bin, back into its folder, under <paramref name="name"/> (its own name, or a
    /// numbered one when that is taken meanwhile). The folder must not be in the recycle bin (the caller checks).
    /// </summary>
    public void Restore(string name, string by, DateTimeOffset now)
    {
        if (!IsDeleted) throw new DomainException($"'{Name}' is not in the recycle bin.");
        DeletedAt = null;
        DeletedBy = null;
        SetName(name);
        Touch(by, now);
        Happened(StorageEventTypes.Restored, by, now);
    }

    /// <summary>
    /// An earlier version becomes the current one again, as a new version with the same bytes (Windows' "Restore"
    /// in Previous Versions); the history is kept. Returns the new version, whose blob gains a reference.
    /// </summary>
    /// <param name="scan">The scan state of the old version's bytes (its blob's).</param>
    public FileVersion RestoreVersion(int number, string by, DateTimeOffset now, ScanStatus scan = ScanStatus.Clean)
    {
        var old = VersionOf(number);
        if (number == CurrentVersion) throw new DomainException($"Version {number} is the current version.");
        AddVersion(FileVersion.Create(old.BlobKey, old.Size, old.ContentType, by, now), by, now, scan);
        return Current!;
    }

    /// <summary>Removes an earlier version (never the current one). Returns it: its blob loses a reference.</summary>
    public FileVersion RemoveVersion(int number, string by, DateTimeOffset now)
    {
        var old = VersionOf(number);
        if (number == CurrentVersion) throw new DomainException("The current version cannot be removed.");
        _versions.Remove(old);
        Touch(by, now);
        return old;
    }

    private FileVersion VersionOf(int number)
    {
        if (IsFolder) throw new DomainException("Folders have no versions.");
        EnsureNotDeleted();
        return _versions.FirstOrDefault(v => v.Number == number) ?? throw new DomainException($"'{Name}' has no version {number}.");
    }

    /// <summary>Sets the item's own entries and whether it inherits (the Security dialog's "Apply").</summary>
    public void SetAccess(bool inheritsAccess, IEnumerable<AccessEntry> entries, string by, DateTimeOffset now)
    {
        EnsureNotDeleted();
        var list = entries.ToList();
        if (!IsFolder) list = list.Select(e => e.Inheritance == AceInheritance.None ? e : e.WithInheritance(AceInheritance.None)).ToList();
        if (list.Count > 200) throw new DomainException("An item can have at most 200 entries.");
        if (Role is FolderRole.Root or FolderRole.Homes) inheritsAccess = false; // nothing above them
        _entries.Clear();
        _entries.AddRange(list);
        InheritsAccess = inheritsAccess;
        Touch(by, now);
    }

    /// <summary>Makes a folder public (its files readable by anyone without signing in) or private again.</summary>
    public void SetPublic(bool isPublic, string by, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (!IsFolder) throw new DomainException("Only folders can be public; a file is public when a folder above it is.");
        if (Role is FolderRole.Homes) throw new DomainException($"'{Name}' cannot be public.");
        if (IsPublic == isPublic) return;
        IsPublic = isPublic;
        Touch(by, now);
    }

    public void SetOwner(string ownerId, string by, DateTimeOffset now)
    {
        EnsureNotDeleted();
        OwnerId = Require(ownerId);
        Touch(by, now);
    }

    private void SetName(string name)
    {
        Name = ItemNames.Require(name);
        NameKey = ItemNames.Key(Name);
    }

    private void Place(Item? parent)
    {
        ParentId = parent?.Id;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Path = (parent?.Path ?? "/") + Id.ToString("N") + "/";
    }

    /// <summary>The event for the outbox, when this is not a system folder (they are made and kept by the module).</summary>
    private void Happened(string type, string by, DateTimeOffset now, string? detail = null)
    {
        if (IsSystem) return;
        Raise(new ItemEvent(type, Id, Kind, Name, ParentId, IsFolder ? null : CurrentVersion, by, now, detail));
    }

    /// <summary>The event for the outbox when the item is removed for good (the purge removes rows; nothing else raises it).</summary>
    public void Purged(int items, string by, DateTimeOffset now) => Happened(StorageEventTypes.Purged, by, now, items.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void Touch(string by, DateTimeOffset now)
    {
        ModifiedAt = now;
        ModifiedBy = by;
        Version++;
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted) throw new DomainException($"'{Name}' is in the recycle bin.");
    }

    private void EnsureEditable()
    {
        EnsureNotDeleted();
        if (IsSystem) throw new DomainException($"'{Name}' is a system folder: it cannot be renamed, moved or deleted.");
    }

    private static string Require(string userId) =>
        string.IsNullOrWhiteSpace(userId) || userId.Length > UserIdLength ? throw new DomainException("A user id is required.") : userId;
}
