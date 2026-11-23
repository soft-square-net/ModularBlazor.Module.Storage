using soft.square.net.WebApi.Modules.Storage.UseCases.Access;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>An item's properties: the details pane and the Properties dialog (needs Read attributes).</summary>
public sealed record GetItemQuery(Guid Id) : IQuery<Result<ItemDetail>>;

public sealed class GetItemHandler(IStorageStore store, ItemAccess access) : IQueryHandler<GetItemQuery, Result<ItemDetail>>
{
    public async ValueTask<Result<ItemDetail>> Handle(GetItemQuery query, CancellationToken ct)
    {
        var reached = await access.ReachAsync(query.Id, StorageRights.ReadAttributes, ct);
        if (!reached.IsSuccess) return reached.As<ItemDetail>();
        var item = reached.Value.Item;
        var subfolders = item.IsFolder ? await store.WithSubfoldersAsync([item.Id], ct) : new HashSet<Guid>();
        var versions = item.Versions.OrderByDescending(v => v.Number)
            .Select(v => new VersionDto(v.Number, v.Size, v.ContentType, v.CreatedAt, v.CreatedBy)).ToArray();
        return new ItemDetail(ItemDto.From(item, reached.Value.Rights, subfolders.Contains(item.Id)), ItemViews.Path(reached.Value.Chain), versions);
    }
}
