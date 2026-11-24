using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>What one sweep of a tenant did.</summary>
public sealed record SweepResult(int ItemsPurged, int BlobsDeleted, long BytesFreed, int UploadsExpired);

/// <summary>
/// The current tenant's housekeeping, run by the sweeper every <see cref="StorageOptions.SweepInterval"/> (and by Storage
/// admins on demand): items in the recycle bin longer than <see cref="StorageOptions.RecycleBinDays"/> are purged,
/// unfinished uploads past their lifetime are aborted, and blobs nobody uses any more are deleted from their stores.
/// It works in batches, so a big backlog takes a few sweeps. Needs no caller.
/// </summary>
public sealed record SweepCommand : ICommand<Result<SweepResult>>;

public sealed class SweepHandler(IStorageStore store, IBlobStores stores, Purger purger, TimeProvider clock, IOptions<StorageOptions> options,
    ILoggerFactory loggers) : ICommandHandler<SweepCommand, Result<SweepResult>>
{
    private const int MaxBatches = 10;
    private readonly ILogger _log = loggers.CreateLogger("soft.square.net.WebApi.Modules.Storage.UseCases.Sweep");

    public async ValueTask<Result<SweepResult>> Handle(SweepCommand command, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var purged = 0;
        if (options.Value.RecycleBinDays > 0)
        {
            var before = now.AddDays(-options.Value.RecycleBinDays);
            for (var batch = 0; batch < MaxBatches; batch++)
            {
                var expired = await store.DeletedBeforeAsync(before, Purger.Batch, ct);
                foreach (var item in expired)
                {
                    // An earlier purge in this batch may have taken it (it was below a purged folder).
                    if (await store.ChainAsync(item.Id, ct) is null) continue;
                    purged += await purger.PurgeAsync(item, ct);
                }
                if (expired.Count < Purger.Batch) break;
            }
        }

        var uploads = 0;
        foreach (var upload in await store.ExpiredUploadsAsync(now, Purger.Batch, ct))
        {
            try
            {
                await stores.Get(upload.Location ?? "").AbortUploadAsync(upload.BlobKey, upload.ProviderUploadId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Storage: could not abort the expired upload {Upload} in its store; forgetting it anyway.", upload.Id);
            }
            store.Remove(upload);
            uploads++;
        }
        if (uploads > 0) await store.SaveAsync(ct);

        var (blobs, bytes) = await purger.DeleteUnusedBlobsAsync(ct, MaxBatches);
        return new SweepResult(purged, blobs, bytes, uploads);
    }
}
