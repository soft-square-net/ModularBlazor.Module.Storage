namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// A folder or file as the Explorer shows it. <see cref="Rights"/> are the caller's effective rights (StorageRights
/// flags, NTFS values), so the client can enable commands; the server checks again on every call.
/// </summary>
public sealed record ItemDto(
    Guid Id,
    ItemKind Kind,
    FolderRole Role,
    Guid? ParentId,
    string Name,
    string? Extension,
    long Size,
    string? ContentType,
    int CurrentVersion,
    string OwnerId,
    bool InheritsAccess,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy,
    int Version,
    int Rights,
    bool HasSubfolders)
{
    public static ItemDto From(Item item, StorageRights rights, bool hasSubfolders = false) => new(
        item.Id, item.Kind, item.Role, item.ParentId, item.Name, item.Extension, item.Size, item.ContentType, item.CurrentVersion,
        item.OwnerId, item.InheritsAccess, item.CreatedAt, item.CreatedBy, item.ModifiedAt, item.ModifiedBy, item.Version,
        (int)rights, hasSubfolders);
}

/// <summary>One level of the address bar.</summary>
public sealed record Crumb(Guid Id, string Name, FolderRole Role);

/// <summary>A folder opened in the Explorer: the folder, its path for the address bar, and the items the caller may see.</summary>
public sealed record FolderView(ItemDto Folder, IReadOnlyList<Crumb> Path, IReadOnlyList<ItemDto> Items);

/// <summary>Where the navigation pane starts: the tenant's shared files and the caller's home folder.</summary>
public sealed record Roots(ItemDto Files, ItemDto Home);

/// <summary>An item with its versions (the details pane, properties).</summary>
public sealed record ItemDetail(ItemDto Item, IReadOnlyList<Crumb> Path, IReadOnlyList<VersionDto> Versions);

public sealed record VersionDto(int Number, long Size, string ContentType, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>What happened to each item of a batch (move, copy, delete): the result, or why it was skipped.</summary>
public sealed record BatchResult(IReadOnlyList<ItemDto> Done, IReadOnlyList<BatchFailure> Failed);

public sealed record BatchFailure(Guid Id, string Reason);

/// <summary>A short-lived URL to download or show a file.</summary>
public sealed record DownloadLink(Uri Url, DateTimeOffset ExpiresAt);
