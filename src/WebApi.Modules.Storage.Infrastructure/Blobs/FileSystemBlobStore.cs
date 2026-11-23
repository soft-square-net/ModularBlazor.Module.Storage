using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

/// <summary>Settings of the local disk provider (section "Storage:Local").</summary>
public sealed class LocalBlobOptions
{
    /// <summary>
    /// The folder that holds the blobs. Every replica that serves Storage must see the same folder (a shared volume).
    /// Empty: %LocalAppData%/ModularBlazor/storage.
    /// </summary>
    public string? Root { get; set; }

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModularBlazor", "storage")
        : Path.GetFullPath(Root);
}

/// <summary>
/// Blobs as files under <see cref="LocalBlobOptions.Root"/>, at their keys (<c>t/acme/2026/11/0193...</c>). An upload
/// is a staging file in <c>.uploads/</c>, written chunk by chunk at its offset, then moved to the key. No presigned
/// URLs: the module serves the bytes (with ranges) behind its own signed links.
/// </summary>
internal sealed partial class FileSystemBlobStore(IOptions<LocalBlobOptions> options) : IBlobStore
{
    private const int BufferSize = 81920;
    private readonly string _root = options.Value.ResolvedRoot;

    public Task<string?> StartUploadAsync(string key, string contentType, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingFolder);
        return Task.FromResult<string?>(Guid.NewGuid().ToString("N"));
    }

    public async Task<string?> WritePartAsync(string key, string? uploadId, int partNumber, long offset, Stream data, long length, CancellationToken ct)
    {
        await using var file = new FileStream(Staging(uploadId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        file.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[BufferSize];
        long written = 0;
        int read;
        while (written < length && (read = await data.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - written)), ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }
        if (written != length) throw new IOException($"The chunk ended after {written} of {length} bytes.");
        return null;
    }

    public Task CompleteUploadAsync(string key, string? uploadId, IReadOnlyList<string> receipts, long size, CancellationToken ct)
    {
        var staging = Staging(uploadId);
        if (size == 0 && !File.Exists(staging)) File.WriteAllBytes(staging, []);
        using (var file = new FileStream(staging, FileMode.Open, FileAccess.Write))
        {
            if (file.Length < size) throw new IOException($"The upload has {file.Length} of {size} bytes.");
            file.SetLength(size); // a retried chunk may have left bytes past the end
        }
        var target = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(staging, target, overwrite: false);
        return Task.CompletedTask;
    }

    public Task AbortUploadAsync(string key, string? uploadId, CancellationToken ct)
    {
        File.Delete(Staging(uploadId));
        return Task.CompletedTask;
    }

    public Task<BlobContent?> OpenAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        if (!File.Exists(path)) return Task.FromResult<BlobContent?>(null);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        return Task.FromResult<BlobContent?>(new BlobContent(stream, stream.Length));
    }

    public Task<Uri?> GetDownloadUrlAsync(string key, string fileName, string contentType, bool inline, TimeSpan lifetime, CancellationToken ct) =>
        Task.FromResult<Uri?>(null);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    private string StagingFolder => Path.Combine(_root, ".uploads");

    private string Staging(string? uploadId) =>
        uploadId is not null && UploadIdPattern().IsMatch(uploadId)
            ? Path.Combine(StagingFolder, uploadId)
            : throw new ArgumentException("Invalid upload id.", nameof(uploadId));

    /// <summary>The file of a key. Keys are made by the module (<c>Blob.NewKey</c>); anything else is refused.</summary>
    private string PathOf(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Invalid blob key.", nameof(key));
        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("Invalid blob key.", nameof(key));
    }

    [GeneratedRegex("^t/[a-z0-9][a-z0-9._-]{0,63}/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex UploadIdPattern();
}
