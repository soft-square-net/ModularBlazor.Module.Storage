using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>Opens a folder: its items that the caller has any right on (access-based enumeration), folders first.</summary>
public sealed record ListFolderQuery(Guid FolderId) : IQuery<Result<FolderView>>;

public sealed class ListFolderHandler(IStorageStore store, ItemAccess access, ThumbnailLinks thumbnails) : IQueryHandler<ListFolderQuery, Result<FolderView>>
{
    public async ValueTask<Result<FolderView>> Handle(ListFolderQuery query, CancellationToken ct)
    {
        var reached = await access.ReachAsync(query.FolderId, StorageRights.ListFolder, ct);
        if (!reached.IsSuccess) return reached.As<FolderView>();
        var folder = reached.Value;
        if (!folder.Item.IsFolder) return Result<FolderView>.Invalid(new ValidationError("This item is a file, not a folder."));

        var visible = new List<(Item Item, StorageRights Rights)>();
        foreach (var child in await store.ChildrenAsync(folder.Item.Id, ct))
        {
            var rights = await access.RightsOnChildAsync(folder, child, ct);
            if (rights != StorageRights.None) visible.Add((child, rights));
        }
        var subfolders = await store.WithSubfoldersAsync(visible.Where(v => v.Item.IsFolder).Select(v => v.Item.Id).ToArray(), ct);

        var sorted = visible
            .OrderBy(v => v.Item.IsFolder ? 0 : 1).ThenBy(v => v.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var items = await thumbnails.AddAsync(sorted.Select(v => v.Item).ToArray(),
            sorted.Select(v => ItemDto.From(v.Item, v.Rights, subfolders.Contains(v.Item.Id))).ToArray(), ct);
        return new FolderView(ItemDto.From(folder.Item, folder.Rights, items.Any(i => i.Kind == ItemKind.Folder)), ItemViews.Path(folder.Chain), items,
            folder.Chain.LastOrDefault(i => i.IsPublic)?.Id);
    }
}

public static class ItemViews
{
    /// <summary>The address bar's levels, top folder first, the item last.</summary>
    public static IReadOnlyList<Crumb> Path(IReadOnlyList<Item> chain) => chain.Select(i => new Crumb(i.Id, i.Name, i.Role)).ToArray();
}
