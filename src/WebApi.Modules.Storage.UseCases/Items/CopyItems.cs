using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// Copy and paste. Needs read on each item (List folder / Read data) and Create files / Create folders on the
/// destination. A folder is copied with everything below it that the caller may read. Copies are owned by the caller,
/// have no entries of their own and inherit from the destination (a copy in Windows). File copies share the source's
/// current blob, so nothing is copied in the provider. A taken name becomes "Name - Copy".
/// </summary>
public sealed record CopyItemsCommand(IReadOnlyList<Guid> Ids, Guid TargetFolderId) : ICommand<Result<BatchResult>>;

public sealed class CopyItemsHandler(IStorageStore store, ItemAccess access, StorageCaller caller, IOptions<StorageOptions> options)
    : ICommandHandler<CopyItemsCommand, Result<BatchResult>>
{
    public async ValueTask<Result<BatchResult>> Handle(CopyItemsCommand command, CancellationToken ct)
    {
        var target = await access.ReachAsync(command.TargetFolderId, StorageRights.None, ct);
        if (!target.IsSuccess) return target.As<BatchResult>();
        if (!target.Value.Item.IsFolder) return Result<BatchResult>.Invalid(new ValidationError("Items can only be pasted into a folder."));

        var done = new List<ItemDto>();
        var failed = new List<BatchFailure>();
        foreach (var id in command.Ids.Distinct())
        {
            var reached = await access.ReachAsync(id, StorageRights.ListFolder, ct);
            if (!reached.IsSuccess) { failed.Add(new BatchFailure(id, string.Join(" ", reached.Errors))); continue; }
            var source = reached.Value.Item;
            if (!target.Value.Has(ItemAccess.CreateRight(source.Kind))) { failed.Add(new BatchFailure(id, StorageMessages.Denied)); continue; }
            if (source.IsFolder && target.Value.Item.Path.StartsWith(source.Path, StringComparison.Ordinal))
            {
                failed.Add(new BatchFailure(id, "The destination folder is a subfolder of the source folder."));
                continue;
            }
            try
            {
                var copy = await CopyAsync(reached.Value, target.Value.Item, ct);
                if (copy is null) { failed.Add(new BatchFailure(id, $"A copy can create at most {options.Value.MaxCopyItems:N0} items.")); continue; }
                done.Add(ItemDto.From(copy, await access.RightsOnChildAsync(target.Value, copy, ct)));
            }
            catch (DomainException e) { failed.Add(new BatchFailure(id, e.Message)); }
        }
        return new BatchResult(done, failed);
    }

    private async Task<Item?> CopyAsync(Reached source, Item destination, CancellationToken ct)
    {
        var me = caller.UserId;
        var now = caller.Now;
        var name = await FreeNames.FindAsync(store, destination.Id, source.Item.Name, numberIfTaken: true,
            (n, i) => ItemNames.CopyName(n, i - 1, source.Item.IsFolder), ct) ?? throw new DomainException(StorageMessages.NameTaken(source.Item.Name));

        var created = new List<Item>();
        var top = Clone(source.Item, destination, name, me, now);
        created.Add(top);

        if (source.Item.IsFolder)
        {
            // Old id → (its chain, the new copy); descendants come by depth, so a parent is always mapped first.
            var map = new Dictionary<Guid, (IReadOnlyList<Item> Chain, Item Copy)> { [source.Item.Id] = (source.Chain, top) };
            foreach (var item in await store.LiveDescendantsAsync(source.Item, ct))
            {
                if (item.ParentId is not { } parentId || !map.TryGetValue(parentId, out var parent)) continue;
                var chain = (IReadOnlyList<Item>)[.. parent.Chain, item];
                if (!(await caller.RightsAsync(chain, ct)).HasFlag(StorageRights.ListFolder)) continue; // skip what the caller cannot read
                var copy = Clone(item, parent.Copy, item.Name, me, now);
                created.Add(copy);
                if (created.Count > options.Value.MaxCopyItems) return null;
                if (item.IsFolder) map[item.Id] = (chain, copy);
            }
        }

        var keys = created.Where(c => !c.IsFolder).Select(c => c.Current!.BlobKey).ToArray();
        var blobs = (await store.BlobsAsync(keys.Distinct().ToArray(), ct)).ToDictionary(b => b.Id);
        foreach (var key in keys)
            if (blobs.TryGetValue(key, out var blob)) blob.AddReference();
        foreach (var item in created) store.Add(item);
        await store.SaveAsync(ct);
        return top;
    }

    private static Item Clone(Item source, Item parent, string name, string by, DateTimeOffset now)
    {
        if (source.IsFolder) return Item.CreateFolder(parent, name, by, now);
        var current = source.Current ?? throw new DomainException($"'{source.Name}' has no content.");
        // Same bytes, same verdict: a copy of a file waiting for its scan waits too.
        return Item.CreateFile(parent, name, by, FileVersion.Create(current.BlobKey, current.Size, current.ContentType, by, now), now, source.Scan);
    }
}
