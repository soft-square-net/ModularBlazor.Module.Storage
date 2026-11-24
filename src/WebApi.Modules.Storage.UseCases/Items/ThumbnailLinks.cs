using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>
/// Short-lived links to the thumbnails of listed files (an &lt;img&gt; sends no token): presigned by the blob's store,
/// or signed by the module. Only clean files with a thumbnail get one.
/// </summary>
public sealed class ThumbnailLinks(IStorageStore store, IBlobStores stores, IDownloadTickets tickets, ITenantContext tenant, IOptions<StorageOptions> options)
{
    /// <summary>The DTOs with <see cref="ItemDto.ThumbnailUrl"/> set where there is a thumbnail.</summary>
    public async Task<IReadOnlyList<ItemDto>> AddAsync(IReadOnlyList<Item> items, IReadOnlyList<ItemDto> dtos, CancellationToken ct)
    {
        var current = items.Where(i => !i.IsFolder && i.Scan == ScanStatus.Clean && i.Current is not null)
            .ToDictionary(i => i.Id, i => i.Current!.BlobKey);
        if (current.Count == 0) return dtos;
        var blobs = (await store.BlobsAsync(current.Values.Distinct().ToArray(), ct))
            .Where(b => b.Thumbnail == ThumbnailState.Ready && b.Scan == ScanStatus.Clean).ToDictionary(b => b.Id);
        if (blobs.Count == 0) return dtos;

        var lifetime = options.Value.DownloadLinkLifetime;
        var result = new List<ItemDto>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (current.TryGetValue(dto.Id, out var key) && blobs.TryGetValue(key, out var blob))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(dto.Name) + ".webp";
                var location = blob.Location ?? "";
                var url = await stores.Get(location).GetDownloadUrlAsync(blob.ThumbnailKey, name, "image/webp", true, lifetime, ct)
                          ?? tickets.Link(new DownloadTicket(tenant.Required.Id, blob.ThumbnailKey, name, "image/webp", true, location), lifetime);
                result.Add(dto with { ThumbnailUrl = url });
            }
            else result.Add(dto);
        }
        return result;
    }
}
