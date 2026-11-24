using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Processing;

/// <summary>A virus scanner (ClamAV in Infrastructure). Throws when it cannot scan (down, timed out): the file waits for the next round.</summary>
public interface IFileScanner
{
    /// <summary>Whether a scanner is configured. Without one, new files are clean at once.</summary>
    bool Enabled { get; }

    /// <summary>Reads the bytes; returns the threat's name, or null when they are clean.</summary>
    Task<string?> ScanAsync(Stream content, CancellationToken ct);
}

/// <summary>Makes thumbnails (ImageSharp in Infrastructure).</summary>
public interface IThumbnailer
{
    /// <summary>A WebP image whose longest side is at most <paramref name="size"/> pixels, or null when the bytes are not an image it can read.</summary>
    Task<byte[]?> MakeAsync(Stream image, int size, CancellationToken ct);
}

/// <summary>Tells the processor that a tenant has new files, so they are scanned now rather than at the next round.</summary>
public interface IStorageWork
{
    void Notify(string tenantId);
}

public static class Thumbnails
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "image/tiff"
    };

    /// <summary>Whether a new file of this type and size gets a thumbnail.</summary>
    public static bool Supports(string? contentType, long size, StorageOptions options) =>
        contentType is not null && Types.Contains(contentType) && size > 0 && size <= options.ThumbnailMaxBytes;
}

public static class BlobWrites
{
    /// <summary>Writes small bytes as one blob (a one-part upload: the same path on every store).</summary>
    public static async Task PutAsync(IBlobStore store, string key, string contentType, byte[] bytes, CancellationToken ct)
    {
        var upload = await store.StartUploadAsync(key, contentType, ct);
        try
        {
            using var data = new MemoryStream(bytes, writable: false);
            var receipt = await store.WritePartAsync(key, upload, 1, 0, data, bytes.Length, ct);
            await store.CompleteUploadAsync(key, upload, receipt is null ? [] : [receipt], bytes.Length, ct);
        }
        catch
        {
            try { await store.AbortUploadAsync(key, upload, CancellationToken.None); } catch { /* the sweep of the store's own leftovers is the provider's */ }
            throw;
        }
    }
}
