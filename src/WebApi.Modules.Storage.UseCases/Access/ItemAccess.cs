using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Access;

/// <summary>An item the caller reached: its chain of folders (item last) and the caller's rights on it.</summary>
public sealed record Reached(IReadOnlyList<Item> Chain, StorageRights Rights)
{
    public Item Item => Chain[^1];

    public bool Has(StorageRights rights) => (Rights & rights) == rights;
}

/// <summary>The access checks the use cases share: load an item with its folders, then check the caller's rights on it.</summary>
public sealed class ItemAccess(IStorageStore store, StorageCaller caller)
{
    /// <summary>
    /// The item with the caller's rights, if it exists, is not in (or under a folder in) the recycle bin, and the caller
    /// holds every right in <paramref name="required"/> on it.
    /// </summary>
    public async Task<Result<Reached>> ReachAsync(Guid id, StorageRights required, CancellationToken ct)
    {
        var chain = await store.ChainAsync(id, ct);
        if (chain is null || chain.Any(i => i.IsDeleted)) return Result<Reached>.NotFound(StorageMessages.NotFound);
        var reached = new Reached(chain, await caller.RightsAsync(chain, ct));
        return reached.Has(required) ? reached : Result<Reached>.Forbidden(StorageMessages.Denied);
    }

    /// <summary>The rights on another item of a chain already loaded (a child of <paramref name="folder"/>).</summary>
    public Task<StorageRights> RightsOnChildAsync(Reached folder, Item child, CancellationToken ct) =>
        caller.RightsAsync([.. folder.Chain, child], ct);

    /// <summary>
    /// May the caller take the item out of its folder (delete, rename, move away)? NTFS: Delete on the item, or Delete
    /// children on its folder.
    /// </summary>
    public async Task<bool> CanRemoveAsync(Reached item, CancellationToken ct) =>
        item.Has(StorageRights.Delete)
        || item.Chain.Count > 1 && (await caller.RightsAsync(item.Chain.Take(item.Chain.Count - 1).ToArray(), ct)).HasFlag(StorageRights.DeleteChildren);

    /// <summary>The right a folder needs for a new item of this kind: Create files or Create folders.</summary>
    public static StorageRights CreateRight(ItemKind kind) =>
        kind == ItemKind.Folder ? StorageRights.CreateFolders : StorageRights.CreateFiles;
}
