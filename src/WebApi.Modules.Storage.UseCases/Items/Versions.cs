using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// Previous versions: an earlier version becomes current again as a new version (same bytes, no copy in the store).
/// Needs Write data (Create files) on the file. Answers the file's details with the new list of versions.
/// </summary>
public sealed record RestoreVersionCommand(Guid Id, int Number) : ICommand<Result<ItemDetail>>;

public sealed class RestoreVersionHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<RestoreVersionCommand, Result<ItemDetail>>
{
    public async ValueTask<Result<ItemDetail>> Handle(RestoreVersionCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.CreateFiles, ct);
        if (!reached.IsSuccess) return reached.As<ItemDetail>();
        var item = reached.Value.Item;
        if (item.IsFolder || item.Versions.All(v => v.Number != command.Number)) return Result<ItemDetail>.NotFound($"'{item.Name}' has no version {command.Number}.");

        var key = item.Versions.First(v => v.Number == command.Number).BlobKey;
        var blobs = await store.BlobsAsync([key], ct);
        var restored = item.RestoreVersion(command.Number, caller.UserId, caller.Now, blobs.FirstOrDefault()?.Scan ?? ScanStatus.Clean);
        foreach (var blob in blobs) blob.AddReference();
        await store.SaveAsync(ct);
        return await ItemDetails.OfAsync(store, reached.Value, ct);
    }
}

/// <summary>Removes an earlier version (never the current one); its bytes go when no other version or copy uses them.</summary>
public sealed record DeleteVersionCommand(Guid Id, int Number) : ICommand<Result<ItemDetail>>;

public sealed class DeleteVersionHandler(IStorageStore store, ItemAccess access, StorageCaller caller, Purger purger) : ICommandHandler<DeleteVersionCommand, Result<ItemDetail>>
{
    public async ValueTask<Result<ItemDetail>> Handle(DeleteVersionCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.CreateFiles, ct);
        if (!reached.IsSuccess) return reached.As<ItemDetail>();
        var item = reached.Value.Item;
        if (item.IsFolder || item.Versions.All(v => v.Number != command.Number)) return Result<ItemDetail>.NotFound($"'{item.Name}' has no version {command.Number}.");

        var removed = item.RemoveVersion(command.Number, caller.UserId, caller.Now);
        await purger.ReleaseAsync(removed.BlobKey, ct); // saves the item too
        await purger.DeleteUnusedBlobsAsync(ct);
        return await ItemDetails.OfAsync(store, reached.Value, ct);
    }
}

/// <summary>The details pane's answer for an item: properties, path, versions (newest first).</summary>
public static class ItemDetails
{
    public static async Task<ItemDetail> OfAsync(IStorageStore store, Reached reached, CancellationToken ct)
    {
        var item = reached.Item;
        var subfolders = item.IsFolder ? await store.WithSubfoldersAsync([item.Id], ct) : new HashSet<Guid>();
        var versions = item.Versions.OrderByDescending(v => v.Number)
            .Select(v => new VersionDto(v.Number, v.Size, v.ContentType, v.CreatedAt, v.CreatedBy)).ToArray();
        return new ItemDetail(ItemDto.From(item, reached.Rights, subfolders.Contains(item.Id)), ItemViews.Path(reached.Chain), versions);
    }
}
