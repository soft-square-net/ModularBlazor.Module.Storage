using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.EventAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.PersonAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.ShareAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// The module's data for the use cases (the current tenant's database), and its unit of work: <see cref="Add"/> and
/// <see cref="Remove"/> stage changes, changes to loaded entities are tracked, <see cref="SaveAsync"/> writes everything
/// in one transaction.
/// </summary>
public interface IStorageStore
{
    /// <summary>The item and the folders above it, from the top down (item last); null when the item does not exist.</summary>
    Task<IReadOnlyList<Item>?> ChainAsync(Guid id, CancellationToken ct);

    /// <summary>A folder's items that are not in the recycle bin.</summary>
    Task<IReadOnlyList<Item>> ChildrenAsync(Guid folderId, CancellationToken ct);

    /// <summary>A folder's item with this name (without case), not deleted.</summary>
    Task<Item?> ChildByNameAsync(Guid folderId, string name, CancellationToken ct);

    /// <summary>The names (keys) used in a folder that start like <paramref name="name"/>, for "keep both" numbering.</summary>
    Task<IReadOnlySet<string>> NameKeysAsync(Guid folderId, CancellationToken ct);

    /// <summary>Which of these folders have subfolders that are not deleted (the navigation tree's expand arrows).</summary>
    Task<IReadOnlySet<Guid>> WithSubfoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct);

    /// <summary>Everything below a folder that is not deleted and not under a deleted folder, by depth.</summary>
    Task<IReadOnlyList<Item>> LiveDescendantsAsync(Item folder, CancellationToken ct);

    /// <summary>Everything below a folder, deleted or not (a purge removes it all).</summary>
    Task<IReadOnlyList<Item>> SubtreeAsync(Item folder, CancellationToken ct);

    /// <summary>Items deleted themselves (not those only under a deleted folder), newest first; only <paramref name="deletedBy"/>'s when given.</summary>
    Task<IReadOnlyList<Item>> RecycleBinAsync(string? deletedBy, CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> items deleted before <paramref name="before"/>, oldest first (the bin's retention).</summary>
    Task<IReadOnlyList<Item>> DeletedBeforeAsync(DateTimeOffset before, int take, CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> blobs no version uses any more.</summary>
    Task<IReadOnlyList<Blob>> UnusedBlobsAsync(int take, CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> uploads that can no longer be resumed.</summary>
    Task<IReadOnlyList<UploadSession>> ExpiredUploadsAsync(DateTimeOffset now, int take, CancellationToken ct);

    /// <summary>Levels below a folder (0 when it has no subfolders).</summary>
    Task<int> SubtreeHeightAsync(Item folder, CancellationToken ct);

    Task<Item?> TopFolderAsync(FolderRole role, CancellationToken ct);

    Task<Item?> HomeAsync(Guid homesId, string userId, CancellationToken ct);

    Task<IReadOnlyList<Blob>> BlobsAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

    Task<UploadSession?> UploadAsync(Guid id, CancellationToken ct);

    /// <summary>Bytes the tenant keeps (every blob once, however many copies use it) and bytes of uploads under way.</summary>
    Task<(long Used, long Pending)> UsageAsync(CancellationToken ct);

    /// <summary>
    /// Finds a system folder, or creates and saves it; when another request created it at the same moment (the key or
    /// the name collides), returns that one.
    /// </summary>
    Task<Item> EnsureAsync(Func<CancellationToken, Task<Item?>> find, Func<Item> create, CancellationToken ct);

    /// <summary>Items with an explicit allow entry for this user (shared with them), not deleted themselves, newest first.</summary>
    Task<IReadOnlyList<Item>> SharedWithAsync(string userId, int take, CancellationToken ct);

    Task<StoragePerson?> PersonAsync(string userId, CancellationToken ct);

    /// <summary>People whose name or email contains <paramref name="search"/> (lower case), by name.</summary>
    Task<IReadOnlyList<StoragePerson>> SearchPeopleAsync(string search, int take, CancellationToken ct);

    Task<IReadOnlyList<StoragePerson>> PeopleAsync(IReadOnlyCollection<string> userIds, CancellationToken ct);

    /// <summary>An item's share links, oldest first.</summary>
    Task<IReadOnlyList<ShareLink>> LinksOfAsync(Guid itemId, CancellationToken ct);

    Task<ShareLink?> LinkAsync(Guid id, CancellationToken ct);

    Task<ShareLink?> LinkByTokenAsync(string token, CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> events after id <paramref name="after"/> that happened by <paramref name="upTo"/>, oldest first.</summary>
    Task<IReadOnlyList<StorageEvent>> EventsAsync(long after, DateTimeOffset upTo, int take, CancellationToken ct);

    /// <summary>Removes the events that happened before <paramref name="before"/>; returns how many.</summary>
    Task<int> DeleteEventsBeforeAsync(DateTimeOffset before, CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> blobs waiting for the virus scanner, oldest first.</summary>
    Task<IReadOnlyList<Blob>> BlobsToScanAsync(int take, CancellationToken ct);

    /// <summary>Up to <paramref name="take"/> clean blobs waiting for a thumbnail, oldest first.</summary>
    Task<IReadOnlyList<Blob>> BlobsToThumbnailAsync(int take, CancellationToken ct);

    /// <summary>The files (deleted or not) with a version whose bytes are <paramref name="blobKey"/>.</summary>
    Task<IReadOnlyList<Item>> FilesWithBlobAsync(string blobKey, CancellationToken ct);

    void Add<T>(T entity) where T : class;

    void Remove<T>(T entity) where T : class;

    Task SaveAsync(CancellationToken ct);

    /// <summary>Saves a moved item and rewrites the paths of everything below it, in one transaction.</summary>
    Task SaveMoveAsync(Item item, string oldPath, int oldDepth, CancellationToken ct);
}
