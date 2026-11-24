using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// One entry of the recycle bin: the item as it was, where it came from (<see cref="OriginalPath"/>, the folders'
/// names from the top down) and who deleted it when. Only items deleted themselves are entries; what was below a
/// deleted folder comes back with it.
/// </summary>
public sealed record DeletedItemDto(ItemDto Item, Guid? OriginalFolderId, IReadOnlyList<Crumb> OriginalPath, DateTimeOffset DeletedAt, string DeletedBy);

/// <summary>
/// The recycle bin: what the caller deleted (Storage admins: everything deleted in the tenant), newest first.
/// </summary>
public sealed record GetRecycleBinQuery : IQuery<Result<IReadOnlyList<DeletedItemDto>>>;

public sealed class GetRecycleBinHandler(IStorageStore store, StorageCaller caller) : IQueryHandler<GetRecycleBinQuery, Result<IReadOnlyList<DeletedItemDto>>>
{
    public async ValueTask<Result<IReadOnlyList<DeletedItemDto>>> Handle(GetRecycleBinQuery query, CancellationToken ct)
    {
        var mine = await caller.IsAdminAsync(ct) ? null : caller.UserId;
        var list = new List<DeletedItemDto>();
        foreach (var item in await store.RecycleBinAsync(mine, ct))
        {
            var chain = await store.ChainAsync(item.Id, ct);
            if (chain is null) continue; // a broken tree: nothing to restore it into
            var rights = await caller.RightsAsync(chain, ct);
            list.Add(new DeletedItemDto(ItemDto.From(item, rights), item.ParentId, ItemViews.Path(chain.Take(chain.Count - 1).ToArray()),
                item.DeletedAt!.Value, item.DeletedBy ?? ""));
        }
        return list;
    }
}

/// <summary>
/// Restore: items go back to their folders, with what was below them. A folder that is itself in the recycle bin must
/// be restored first. A name taken meanwhile gets a number ("Report (2).docx"). Allowed to whoever deleted the item,
/// and to anyone who could delete it now (Delete, or Delete children on its folder).
/// </summary>
public sealed record RestoreItemsCommand(IReadOnlyList<Guid> Ids) : ICommand<Result<BatchResult>>;

public sealed class RestoreItemsHandler(IStorageStore store, StorageCaller caller, ItemAccess access) : ICommandHandler<RestoreItemsCommand, Result<BatchResult>>
{
    public async ValueTask<Result<BatchResult>> Handle(RestoreItemsCommand command, CancellationToken ct)
    {
        var done = new List<ItemDto>();
        var failed = new List<BatchFailure>();
        foreach (var id in command.Ids.Distinct())
        {
            var bin = await RecycleBinEntry.FindAsync(store, caller, id, ct);
            if (!bin.IsSuccess) { failed.Add(new BatchFailure(id, string.Join(" ", bin.Errors))); continue; }
            var (chain, rights) = bin.Value;
            var item = chain[^1];
            if (chain.Take(chain.Count - 1).FirstOrDefault(f => f.IsDeleted) is { } deletedFolder)
            {
                failed.Add(new BatchFailure(id, $"'{deletedFolder.Name}' is in the recycle bin: restore it first."));
                continue;
            }
            if (item.DeletedBy != caller.UserId && !await access.CanRemoveAsync(new Reached(chain, rights), ct))
            {
                failed.Add(new BatchFailure(id, StorageMessages.Denied));
                continue;
            }
            try
            {
                var name = await FreeNames.FindAsync(store, item.ParentId!.Value, item.Name, numberIfTaken: true,
                    (n, i) => ItemNames.Numbered(n, i, item.IsFolder), ct) ?? throw new DomainException(StorageMessages.NameTaken(item.Name));
                item.Restore(name, caller.UserId, caller.Now);
                await store.SaveAsync(ct);
                done.Add(ItemDto.From(item, rights));
            }
            catch (DomainException e) { failed.Add(new BatchFailure(id, e.Message)); }
        }
        return new BatchResult(done, failed);
    }
}

/// <summary>
/// Delete permanently (from the recycle bin, or Shift+Delete): the items and everything below them are removed, and
/// the bytes no other file or version uses are deleted from the store. Allowed to whoever deleted the item and to
/// Storage admins. Empty ids: the whole bin the caller sees.
/// </summary>
public sealed record PurgeItemsCommand(IReadOnlyList<Guid> Ids) : ICommand<Result<BatchResult>>;

public sealed class PurgeItemsHandler(IStorageStore store, StorageCaller caller, Purger purger) : ICommandHandler<PurgeItemsCommand, Result<BatchResult>>
{
    public async ValueTask<Result<BatchResult>> Handle(PurgeItemsCommand command, CancellationToken ct)
    {
        var admin = await caller.IsAdminAsync(ct);
        var ids = command.Ids.Count > 0
            ? command.Ids.Distinct().ToList()
            : (await store.RecycleBinAsync(admin ? null : caller.UserId, ct)).Select(i => i.Id).ToList();

        var done = new List<ItemDto>();
        var failed = new List<BatchFailure>();
        foreach (var id in ids)
        {
            var bin = await RecycleBinEntry.FindAsync(store, caller, id, ct);
            if (!bin.IsSuccess) { failed.Add(new BatchFailure(id, string.Join(" ", bin.Errors))); continue; }
            var item = bin.Value.Chain[^1];
            if (!admin && item.DeletedBy != caller.UserId) { failed.Add(new BatchFailure(id, StorageMessages.Denied)); continue; }
            var dto = ItemDto.From(item, bin.Value.Rights);
            await purger.PurgeAsync(item, caller.UserId, ct);
            done.Add(dto);
        }
        await purger.DeleteUnusedBlobsAsync(ct);
        return new BatchResult(done, failed);
    }
}

/// <summary>Finds an item in the recycle bin that the caller may see there (theirs, or any for Storage admins).</summary>
internal static class RecycleBinEntry
{
    public static async Task<Result<(IReadOnlyList<Item> Chain, StorageRights Rights)>> FindAsync(IStorageStore store, StorageCaller caller,
        Guid id, CancellationToken ct)
    {
        var chain = await store.ChainAsync(id, ct);
        var item = chain?[^1];
        if (chain is null || item is null || !item.IsDeleted) return Result.NotFound("It is not in the recycle bin.");
        if (item.DeletedBy != caller.UserId && !await caller.IsAdminAsync(ct)) return Result.NotFound("It is not in the recycle bin.");
        return (chain, await caller.RightsAsync(chain, ct));
    }
}

/// <summary>
/// Removes items for good and frees their bytes. Every version of a removed file releases its blob; a blob nobody uses
/// any more is deleted from its store, then forgotten. A blob whose bytes cannot be deleted (the store is down) stays
/// unused and the next sweep tries again.
/// </summary>
public sealed class Purger(IStorageStore store, IBlobStores stores, TimeProvider clock, ILoggerFactory loggers)
{
    public const int Batch = 200;

    private readonly ILogger _log = loggers.CreateLogger("soft.square.net.WebApi.Modules.Storage.UseCases.Purger");

    /// <summary>Removes the item and everything below it (deleted or not), in one save; <paramref name="by"/> is who (or "system").</summary>
    public async Task<int> PurgeAsync(Item item, string by, CancellationToken ct)
    {
        var items = item.IsFolder ? [item, .. await store.SubtreeAsync(item, ct)] : new List<Item> { item };
        item.Purged(items.Count, by, clock.GetUtcNow());
        var keys = items.Where(i => !i.IsFolder).SelectMany(i => i.Versions).Select(v => v.BlobKey).ToList();
        var blobs = (await store.BlobsAsync(keys.Distinct().ToArray(), ct)).ToDictionary(b => b.Id);
        foreach (var key in keys)
            if (blobs.TryGetValue(key, out var blob)) blob.Release();
        // Children first: a database may check the parent key on every delete.
        foreach (var each in items.OrderByDescending(i => i.Depth)) store.Remove(each);
        await store.SaveAsync(ct);
        return items.Count;
    }

    /// <summary>Releases one blob (a removed version) and saves.</summary>
    public async Task ReleaseAsync(string blobKey, CancellationToken ct)
    {
        foreach (var blob in await store.BlobsAsync([blobKey], ct)) blob.Release();
        await store.SaveAsync(ct);
    }

    /// <summary>Deletes the bytes of unused blobs, a batch at a time. Returns how many blobs and bytes were freed.</summary>
    public async Task<(int Blobs, long Bytes)> DeleteUnusedBlobsAsync(CancellationToken ct, int maxBatches = 1)
    {
        var count = 0;
        long bytes = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            var unused = await store.UnusedBlobsAsync(Batch, ct);
            if (unused.Count == 0) break;
            var deleted = new List<Blob>();
            foreach (var blob in unused)
            {
                try
                {
                    var blobStore = stores.Get(blob.Location ?? "");
                    await blobStore.DeleteAsync(blob.Id, ct);
                    if (blob.Thumbnail == ThumbnailState.Ready)
                    {
                        try { await blobStore.DeleteAsync(blob.ThumbnailKey, ct); }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            _log.LogWarning(e, "Storage: could not delete the thumbnail of blob {Blob}; it stays in the store.", blob.Id);
                        }
                    }
                    deleted.Add(blob);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning(e, "Storage: could not delete the bytes of blob {Blob}; the next sweep tries again.", blob.Id);
                }
            }
            foreach (var blob in deleted) store.Remove(blob);
            await store.SaveAsync(ct);
            count += deleted.Count;
            bytes += deleted.Sum(b => b.Size);
            if (deleted.Count < unused.Count || unused.Count < Batch) break; // failures, or nothing left
        }
        return (count, bytes);
    }
}
