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
