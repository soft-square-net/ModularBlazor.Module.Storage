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
        return await ItemDetails.OfAsync(store, reached.Value, ct);
    }
}
