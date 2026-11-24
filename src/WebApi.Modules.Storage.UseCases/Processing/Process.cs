using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;
using soft.square.net.WebApi.Modules.Storage.UseCases.Items;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Processing;

/// <summary>What one round of processing did for a tenant.</summary>
public sealed record ProcessResult(int Scanned, int Infected, int Thumbnails);

/// <summary>
/// The current tenant's new bytes, a batch at a time: the virus scanner reads the blobs waiting for it (their files
/// follow the verdict, and get a <c>file.scanned</c> event), then clean images get a thumbnail. Run by the processor
/// after uploads and every <see cref="StorageOptions.ProcessInterval"/>; safe to run twice. Needs no caller.
/// <para>
/// When the scanner is down, the files wait (and stay blocked). When no scanner is configured any more, files still
/// waiting are let through as clean: whoever removed the scanner chose files without scans.
/// </para>
/// </summary>
public sealed record ProcessCommand : ICommand<Result<ProcessResult>>;

public sealed class ProcessHandler(IStorageStore store, IBlobStores stores, IFileScanner scanner, IThumbnailer thumbnailer, TimeProvider clock,
    IOptions<StorageOptions> options, ILoggerFactory loggers) : ICommandHandler<ProcessCommand, Result<ProcessResult>>
{
    public const int Batch = 20;

    private readonly ILogger _log = loggers.CreateLogger("soft.square.net.WebApi.Modules.Storage.UseCases.Process");

    public async ValueTask<Result<ProcessResult>> Handle(ProcessCommand command, CancellationToken ct)
    {
        var (scanned, infected) = await ScanAsync(ct);
        var thumbnails = await ThumbnailsAsync(ct);
        return new ProcessResult(scanned, infected, thumbnails);
    }

    private async Task<(int Scanned, int Infected)> ScanAsync(CancellationToken ct)
    {
        int scanned = 0, infected = 0;
        foreach (var blob in await store.BlobsToScanAsync(Batch, ct))
        {
            string? threat;
            if (!scanner.Enabled)
            {
                threat = null;
            }
            else
            {
                try
                {
                    var content = await stores.Get(blob.Location ?? "").OpenAsync(blob.Id, ct);
                    if (content is null)
                    {
                        threat = "content missing"; // nothing to hand out anyway; keeps it blocked and out of the queue
                    }
                    else
                    {
                        await using var stream = content.Stream;
                        threat = await scanner.ScanAsync(stream, ct);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // The scanner or the store is down: the rest would fail the same way. They wait for the next round.
                    _log.LogWarning(e, "Storage: could not scan blob {Blob}; new files stay blocked until the scanner answers.", blob.Id);
                    break;
                }
            }

            var now = clock.GetUtcNow();
            blob.Scanned(threat, now);
            foreach (var file in await store.FilesWithBlobAsync(blob.Id, ct)) file.Scanned(blob.Id, threat, now);
            await store.SaveAsync(ct);
            scanned++;
            if (threat is not null)
            {
                infected++;
                _log.LogWarning("Storage: blob {Blob} is infected ({Threat}); its files are quarantined.", blob.Id, threat);
            }
        }
        return (scanned, infected);
    }

    private async Task<int> ThumbnailsAsync(CancellationToken ct)
    {
        var made = 0;
        var size = Math.Clamp(options.Value.ThumbnailSize, 32, 2048);
        foreach (var blob in await store.BlobsToThumbnailAsync(Batch, ct))
        {
            var blobStore = stores.Get(blob.Location ?? "");
            byte[]? thumbnail;
            try
            {
                var content = await blobStore.OpenAsync(blob.Id, ct);
                if (content is null) thumbnail = null;
                else
                {
                    await using var stream = content.Stream;
                    thumbnail = await thumbnailer.MakeAsync(stream, size, ct);
                }
                if (thumbnail is not null) await BlobWrites.PutAsync(blobStore, blob.ThumbnailKey, "image/webp", thumbnail, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(e, "Storage: could not make the thumbnail of blob {Blob}; trying again next round.", blob.Id);
                break;
            }
            blob.ThumbnailMade(thumbnail is not null);
            await store.SaveAsync(ct);
            if (thumbnail is not null) made++;
        }
        return made;
    }
}
