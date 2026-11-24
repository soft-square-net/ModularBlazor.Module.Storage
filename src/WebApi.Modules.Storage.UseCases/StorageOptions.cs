namespace soft.square.net.WebApi.Modules.Storage.UseCases;

/// <summary>The module's settings (section "Storage"; defaults in the manifest, overridden by the admin).</summary>
public sealed class StorageOptions
{
    /// <summary>Largest file accepted, in bytes (default 2 GiB).</summary>
    public long MaxFileSize { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Upload chunk size in bytes (default 8 MiB). An upload may use bigger chunks: S3 needs at least 5 MiB for every
    /// part but the last, and a file is sent in at most <c>UploadSession.MaxParts</c> chunks.
    /// </summary>
    public int ChunkSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>How long an unfinished upload can be resumed.</summary>
    public TimeSpan UploadLifetime { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long a download link (presigned or signed by the module) works.</summary>
    public TimeSpan DownloadLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Days an item stays in the recycle bin before the sweeper removes it for good (0: until the bin is emptied).</summary>
    public int RecycleBinDays { get; set; } = 30;

    /// <summary>How often each tenant is swept (bin retention, expired uploads, unused blobs); zero turns the sweeper off.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Days the event feed (GET /events) keeps an event; the sweeper removes older ones.</summary>
    public int EventRetentionDays { get; set; } = 7;

    /// <summary>
    /// How old an event must be before the feed shows it. Saves running at the same moment may commit their events out
    /// of id order; waiting a little lets the slower one land before a reader moves past its id.
    /// </summary>
    public TimeSpan EventDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often each tenant's new files are scanned and given thumbnails (also right after an upload); zero turns it off.</summary>
    public TimeSpan ProcessInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest side of a thumbnail, in pixels.</summary>
    public int ThumbnailSize { get; set; } = 320;

    /// <summary>Images bigger than this (bytes) get no thumbnail.</summary>
    public long ThumbnailMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>The virus scanner (section "Storage:ClamAV"): ClamAV's clamd, off until a host is set.</summary>
    public ClamAvSettings ClamAV { get; set; } = new();

    /// <summary>Most items a single copy may create (a folder copies everything below it).</summary>
    public int MaxCopyItems { get; set; } = 10_000;

    /// <summary>Where new blobs go, and the quota, for every tenant that has no entry in <see cref="Tenants"/> (section "Storage:Default").</summary>
    public BlobPlacement Default { get; set; } = new();

    /// <summary>Per tenant id (section "Storage:Tenants:{tenant}"): what differs from <see cref="Default"/>.</summary>
    public Dictionary<string, BlobPlacement> Tenants { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A tenant's placement: its own entry over <see cref="Default"/>, setting by setting.</summary>
    public BlobPlacement PlacementOf(string tenantId)
    {
        if (!Tenants.TryGetValue(tenantId, out var own)) return Default;
        return new BlobPlacement
        {
            Connection = own.Connection ?? Default.Connection,
            Bucket = own.Bucket ?? Default.Bucket,
            Prefix = own.Prefix ?? Default.Prefix,
            Quota = own.Quota ?? Default.Quota
        };
    }
}

/// <summary>
/// Where a tenant's new blobs are written: a named connection (section "Storage:Connections:{name}"; empty or "local": the local
/// disk of "Storage:Local"), a bucket in it and a key prefix; and how many bytes the tenant may keep (0 or empty: no limit).
/// Blobs remember where they were written, so changing this moves only new files.
/// </summary>
public sealed class BlobPlacement
{
    public string? Connection { get; set; }
    public string? Bucket { get; set; }
    public string? Prefix { get; set; }
    public long? Quota { get; set; }

    /// <summary>The quota in bytes, or null for none.</summary>
    public long? Limit => Quota is > 0 ? Quota : null;
}

/// <summary>
/// ClamAV's daemon (clamd) over TCP, INSTREAM. With a <see cref="Host"/>, every new file waits for its scan before it
/// can be downloaded; without one, files are clean as soon as they are uploaded.
/// </summary>
public sealed class ClamAvSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 3310;
    /// <summary>How long one scan may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}
