using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// Delete (Del): to the recycle bin. Each item needs Delete, or Delete children on its folder. A deleted folder takes
/// everything below it out of reach with it; nothing below it is changed, so restoring it brings it all back.
/// <see cref="Permanent"/> (Shift+Del): removed for good at once, with the bytes no other file uses.
/// </summary>
public sealed record DeleteItemsCommand(IReadOnlyList<Guid> Ids, bool Permanent = false) : ICommand<Result<BatchResult>>;

public sealed class DeleteItemsHandler(IStorageStore store, ItemAccess access, StorageCaller caller, Purger purger) : ICommandHandler<DeleteItemsCommand, Result<BatchResult>>
{
    public async ValueTask<Result<BatchResult>> Handle(DeleteItemsCommand command, CancellationToken ct)
    {
        var done = new List<ItemDto>();
        var failed = new List<BatchFailure>();
        foreach (var id in command.Ids.Distinct())
        {
            var reached = await access.ReachAsync(id, StorageRights.None, ct);
            if (!reached.IsSuccess) { failed.Add(new BatchFailure(id, string.Join(" ", reached.Errors))); continue; }
            if (!await access.CanRemoveAsync(reached.Value, ct)) { failed.Add(new BatchFailure(id, StorageMessages.Denied)); continue; }
            try
            {
                reached.Value.Item.Delete(caller.UserId, caller.Now);
                await store.SaveAsync(ct);
                done.Add(ItemDto.From(reached.Value.Item, reached.Value.Rights));
                if (command.Permanent) await purger.PurgeAsync(reached.Value.Item, caller.UserId, ct);
            }
            catch (DomainException e) { failed.Add(new BatchFailure(id, e.Message)); }
        }
        if (command.Permanent && done.Count > 0) await purger.DeleteUnusedBlobsAsync(ct);
        return new BatchResult(done, failed);
    }
}
