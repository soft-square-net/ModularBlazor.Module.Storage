namespace soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

/// <summary>Readable bytes of a blob. Seekable when the provider allows ranges (the local disk).</summary>
public sealed record BlobContent(Stream Stream, long Length);

/// <summary>
/// Where the bytes live: a folder on disk, or a bucket in S3 / MinIO (one store per location, see <see cref="IBlobStores"/>).
/// Keys come from <c>Blob.NewKey</c> and never change; the tree of folders and files is in the database only. Uploads
/// are written in parts, in order, then completed, which maps to an S3 multipart upload and to a staging file on disk.
/// </summary>
public interface IBlobStore
{
    /// <summary>The smallest part the provider takes (every part but the last): 5 MiB on S3, 1 byte on disk.</summary>
    int MinPartSize { get; }

    /// <summary>Starts an upload; returns the provider's upload id (S3) or null.</summary>
    Task<string?> StartUploadAsync(string key, string contentType, CancellationToken ct);

    /// <summary>Stores part <paramref name="partNumber"/> (1-based) at <paramref name="offset"/>; returns its receipt (an S3 ETag) or null.</summary>
    Task<string?> WritePartAsync(string key, string? uploadId, int partNumber, long offset, Stream data, long length, CancellationToken ct);

    /// <summary>Makes the uploaded parts the blob <paramref name="key"/>.</summary>
    Task CompleteUploadAsync(string key, string? uploadId, IReadOnlyList<string> receipts, long size, CancellationToken ct);

    /// <summary>Drops an unfinished upload's parts.</summary>
    Task AbortUploadAsync(string key, string? uploadId, CancellationToken ct);

    /// <summary>Opens a blob for reading, or null when it does not exist.</summary>
    Task<BlobContent?> OpenAsync(string key, CancellationToken ct);

    /// <summary>
    /// A short-lived URL the browser can download from directly (S3 presigned GET), or null when the provider has none
    /// and the module serves the bytes itself (the local disk).
    /// </summary>
    Task<Uri?> GetDownloadUrlAsync(string key, string fileName, string contentType, bool inline, TimeSpan lifetime, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}
