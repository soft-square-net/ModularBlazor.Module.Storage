using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>Rename (F2). NTFS: Delete on the item, or Delete children on its folder. Changing only the case is allowed.</summary>
public sealed record RenameItemCommand(Guid Id, string Name, int? Version) : ICommand<Result<ItemDto>>;

public sealed class RenameItemHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<RenameItemCommand, Result<ItemDto>>
{
    public async ValueTask<Result<ItemDto>> Handle(RenameItemCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.Id, StorageRights.None, ct);
        if (!reached.IsSuccess) return reached.As<ItemDto>();
        var item = reached.Value.Item;
        if (!await access.CanRemoveAsync(reached.Value, ct)) return Result<ItemDto>.Forbidden(StorageMessages.Denied);
        if (command.Version is { } version && version != item.Version)
            return Result<ItemDto>.Conflict("Someone else changed this item first; refresh and try again.");

        var name = ItemNames.Require(command.Name);
        if (item.ParentId is { } parentId && await store.ChildByNameAsync(parentId, name, ct) is { } other && other.Id != item.Id)
            return Result<ItemDto>.Conflict(StorageMessages.NameTaken(name));

        item.Rename(name, caller.UserId, caller.Now);
        await store.SaveAsync(ct);
        return ItemDto.From(item, reached.Value.Rights);
    }
}
