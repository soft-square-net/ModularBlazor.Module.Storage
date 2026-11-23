using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// Cut and paste, or drag to another folder. Each item needs to be removable from its folder (Delete, or Delete children
/// on the folder) and the destination needs Create files / Create folders. Moved items keep their own entries and
/// inherit from the new folder (a move within a volume in Windows). Each item is moved on its own; the result lists
/// what was moved and why the others were not.
/// </summary>
public sealed record MoveItemsCommand(IReadOnlyList<Guid> Ids, Guid TargetFolderId) : ICommand<Result<BatchResult>>;

public sealed class MoveItemsHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<MoveItemsCommand, Result<BatchResult>>
{
    public async ValueTask<Result<BatchResult>> Handle(MoveItemsCommand command, CancellationToken ct)
    {
        var target = await access.ReachAsync(command.TargetFolderId, StorageRights.None, ct);
        if (!target.IsSuccess) return target.As<BatchResult>();
        if (!target.Value.Item.IsFolder) return Result<BatchResult>.Invalid(new ValidationError("Items can only be moved into a folder."));

        var done = new List<ItemDto>();
        var failed = new List<BatchFailure>();
        foreach (var id in command.Ids.Distinct())
        {
            var reached = await access.ReachAsync(id, StorageRights.None, ct);
            if (!reached.IsSuccess) { failed.Add(new BatchFailure(id, string.Join(" ", reached.Errors))); continue; }
            var item = reached.Value.Item;
            if (item.ParentId == target.Value.Item.Id) { failed.Add(new BatchFailure(id, "The item is already in this folder.")); continue; }
            if (!target.Value.Has(ItemAccess.CreateRight(item.Kind)) || !await access.CanRemoveAsync(reached.Value, ct))
            {
                failed.Add(new BatchFailure(id, StorageMessages.Denied));
                continue;
            }
            if (await store.ChildByNameAsync(target.Value.Item.Id, item.Name, ct) is not null)
            {
                failed.Add(new BatchFailure(id, StorageMessages.NameTaken(item.Name)));
                continue;
            }
            try
            {
                var height = item.IsFolder ? await store.SubtreeHeightAsync(item, ct) : 0;
                var (oldPath, oldDepth) = item.MoveTo(target.Value.Item, height, caller.UserId, caller.Now);
                await store.SaveMoveAsync(item, oldPath, oldDepth, ct);
                done.Add(ItemDto.From(item, await access.RightsOnChildAsync(target.Value, item, ct)));
            }
            catch (DomainException e) { failed.Add(new BatchFailure(id, e.Message)); }
        }
        return new BatchResult(done, failed);
    }
}
