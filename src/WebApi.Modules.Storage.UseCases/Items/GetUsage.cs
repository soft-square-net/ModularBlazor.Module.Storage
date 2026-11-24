using soft.square.net.WebApi.Contracts.Tenancy;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>The tenant's storage use, for "x GB of y GB used": kept, uploading, and the quota (null: none).</summary>
public sealed record StorageUsage(long Used, long Pending, long? Quota);

public sealed record GetUsageQuery : IQuery<Result<StorageUsage>>;

public sealed class GetUsageHandler(IStorageStore store, ITenantContext tenant, IOptions<StorageOptions> options)
    : IQueryHandler<GetUsageQuery, Result<StorageUsage>>
{
    public async ValueTask<Result<StorageUsage>> Handle(GetUsageQuery query, CancellationToken ct)
    {
        var (used, pending) = await store.UsageAsync(ct);
        return new StorageUsage(used, pending, options.Value.PlacementOf(tenant.Required.Id).Limit);
    }
}
