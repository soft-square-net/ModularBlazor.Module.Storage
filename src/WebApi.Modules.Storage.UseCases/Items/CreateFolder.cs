using soft.square.net.WebApi.Modules.Storage.Core.Common;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// New folder (needs Create folders on the parent). With <see cref="NumberIfTaken"/> a taken name gets a number, as
/// Explorer's "New folder" does ("New folder (2)"); otherwise a taken name is a conflict.
/// </summary>
public sealed record CreateFolderCommand(Guid ParentId, string Name, bool NumberIfTaken) : ICommand<Result<ItemDto>>;

public sealed class CreateFolderHandler(IStorageStore store, ItemAccess access, StorageCaller caller) : ICommandHandler<CreateFolderCommand, Result<ItemDto>>
{
    public async ValueTask<Result<ItemDto>> Handle(CreateFolderCommand command, CancellationToken ct)
    {
        var reached = await access.ReachAsync(command.ParentId, StorageRights.CreateFolders, ct);
        if (!reached.IsSuccess) return reached.As<ItemDto>();
        var parent = reached.Value;

        var name = ItemNames.Require(command.Name);
        var free = await FreeNames.FindAsync(store, parent.Item.Id, name, command.NumberIfTaken, (n, i) => ItemNames.Numbered(n, i, isFolder: true), ct);
        if (free is null) return Result<ItemDto>.Conflict(StorageMessages.NameTaken(name));

        var folder = Item.CreateFolder(parent.Item, free, caller.UserId, caller.Now);
        store.Add(folder);
        await store.SaveAsync(ct);
        return ItemDto.From(folder, await access.RightsOnChildAsync(parent, folder, ct));
    }
}

/// <summary>Finds a free name in a folder: the name itself, or (when allowed) the first numbered variant that is free.</summary>
public static class FreeNames
{
    public static async Task<string?> FindAsync(IStorageStore store, Guid folderId, string name, bool numberIfTaken,
        Func<string, int, string> variant, CancellationToken ct)
    {
        if (await store.ChildByNameAsync(folderId, name, ct) is null) return name;
        if (!numberIfTaken) return null;
        var taken = await store.NameKeysAsync(folderId, ct);
        for (var number = 2; number < 10_000; number++)
        {
            var candidate = variant(name, number);
            if (candidate.Length <= ItemNames.MaxLength && !taken.Contains(ItemNames.Key(candidate))) return candidate;
        }
        return null;
    }
}
