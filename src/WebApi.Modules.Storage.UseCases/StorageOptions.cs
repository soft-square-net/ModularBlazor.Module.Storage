namespace soft.square.net.WebApi.Modules.Storage.UseCases;

/// <summary>The module's settings (section "Storage"; defaults in the manifest, overridden by the admin).</summary>
public sealed class StorageOptions
{
    /// <summary>Largest file accepted, in bytes (default 2 GiB).</summary>
    public long MaxFileSize { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Upload chunk size in bytes (default 8 MiB; S3 needs at least 5 MiB for every part but the last).</summary>
    public int ChunkSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>How long an unfinished upload can be resumed.</summary>
    public TimeSpan UploadLifetime { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long a download link (presigned or signed by the module) works.</summary>
    public TimeSpan DownloadLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Most items a single copy may create (a folder copies everything below it).</summary>
    public int MaxCopyItems { get; set; } = 10_000;
}
