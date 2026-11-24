using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;

namespace soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;

/// <summary>Whether a blob has a thumbnail (images only), stored next to it under <see cref="Blob.ThumbnailKey"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThumbnailState>))]
public enum ThumbnailState
{
    /// <summary>Not an image the module can read, or written before thumbnails existed.</summary>
    None,
    /// <summary>Waiting for the processor (after the scan, when there is one).</summary>
    Pending,
    Ready,
    /// <summary>The image could not be read; not tried again.</summary>
    Failed
}

/// <summary>
/// Bytes in the storage provider, by an immutable key (<c>t/{tenant}/{yyyy}/{MM}/{id}</c>). Copies of a file and its
/// versions share blobs, so a blob counts the versions that use it; the garbage collector removes the ones nobody
/// uses after the recycle bin is emptied. What is known about the bytes lives here too, once for every copy: the virus
/// scan's verdict and the thumbnail.
/// </summary>
public sealed class Blob : AggregateRoot<string>, ITenantOwned
{
    private Blob() { } // EF Core

    public const int LocationLength = 300;
    public const int ThreatLength = 200;

    public string TenantId { get; set; } = "";
    /// <summary>The store the bytes were written to (<c>connection|bucket|prefix</c>; null: the local disk).</summary>
    public string? Location { get; private set; }
    public long Size { get; private set; }
    public int References { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The virus scan: <see cref="ScanStatus.Pending"/> until the scanner has read the bytes (Clean without a scanner).</summary>
    public ScanStatus Scan { get; private set; }
    /// <summary>What the scanner found, when <see cref="Scan"/> is <see cref="ScanStatus.Infected"/>.</summary>
    public string? Threat { get; private set; }
    public DateTimeOffset? ScannedAt { get; private set; }
    public ThumbnailState Thumbnail { get; private set; }

    /// <summary>Where the thumbnail is (same store), when <see cref="Thumbnail"/> is <see cref="ThumbnailState.Ready"/>.</summary>
    public string ThumbnailKey => Id + ".thumb.webp";

    /// <param name="scan">A scanner is configured: the bytes wait for it.</param>
    /// <param name="thumbnail">The bytes are an image to make a thumbnail of.</param>
    public static Blob Create(string key, string location, long size, DateTimeOffset now, bool scan = false, bool thumbnail = false) =>
        new()
        {
            Id = key, Location = location.Length == 0 ? null : location, Size = size, References = 1, CreatedAt = now,
            Scan = scan ? ScanStatus.Pending : ScanStatus.Clean,
            Thumbnail = thumbnail ? ThumbnailState.Pending : ThumbnailState.None
        };

    public void AddReference() => References++;

    public void Release() => References = Math.Max(0, References - 1);

    /// <summary>The scanner's verdict. An infected blob gets no thumbnail.</summary>
    public void Scanned(string? threat, DateTimeOffset now)
    {
        Scan = threat is null ? ScanStatus.Clean : ScanStatus.Infected;
        Threat = threat is { Length: > ThreatLength } ? threat[..ThreatLength] : threat;
        ScannedAt = now;
        if (Scan == ScanStatus.Infected && Thumbnail == ThumbnailState.Pending) Thumbnail = ThumbnailState.None;
    }

    public void ThumbnailMade(bool made) => Thumbnail = made ? ThumbnailState.Ready : ThumbnailState.Failed;

    /// <summary>A new key for a tenant's blob; never derived from a file name.</summary>
    public static string NewKey(string tenantId, DateTimeOffset now) =>
        $"t/{tenantId}/{now.UtcDateTime:yyyy}/{now.UtcDateTime:MM}/{Guid.CreateVersion7(now):N}";
}
