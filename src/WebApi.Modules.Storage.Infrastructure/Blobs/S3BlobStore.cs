using System.Buffers;
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

/// <summary>
/// Blobs as objects in one bucket of S3 or MinIO, at <c>prefix + key</c>. An upload is an S3 multipart upload (each
/// chunk a part, the ETags kept by the session); an empty file is a plain empty object. Downloads are presigned GETs
/// with the file's name and type, so the bytes go from the bucket to the browser; the module's own content endpoint
/// streams the object (without ranges).
/// </summary>
internal sealed class S3BlobStore(IAmazonS3 client, IAmazonS3 presigner, Protocol publicProtocol, string bucket, string prefix,
    bool createBucket) : IBlobStore
{
    public const int S3MinPartSize = 5 * 1024 * 1024;

    private Task? _bucketReady;

    public int MinPartSize => S3MinPartSize;

    public async Task<string?> StartUploadAsync(string key, string contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        var started = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = ObjectKey(key), ContentType = contentType
        }, ct);
        return started.UploadId;
    }

    public async Task<string?> WritePartAsync(string key, string? uploadId, int partNumber, long offset, Stream data, long length, CancellationToken ct)
    {
        // The request body is not seekable; a part (a few MiB) is buffered so the SDK knows its length and can retry it.
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, length));
        try
        {
            var read = 0;
            int n;
            while (read < length && (n = await data.ReadAsync(buffer.AsMemory(read, (int)length - read), ct)) > 0) read += n;
            if (read != length) throw new IOException($"The chunk ended after {read} of {length} bytes.");
            using var part = new MemoryStream(buffer, 0, read, writable: false);
            var stored = await client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = ObjectKey(key), UploadId = Require(uploadId), PartNumber = partNumber, PartSize = length, InputStream = part
            }, ct);
            return stored.ETag;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task CompleteUploadAsync(string key, string? uploadId, IReadOnlyList<string> receipts, long size, CancellationToken ct)
    {
        if (size == 0)
        {
            // S3 needs at least one part; an empty file is an empty object, and the multipart upload is dropped.
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = ObjectKey(key), InputStream = new MemoryStream([]) }, ct);
            await AbortUploadAsync(key, uploadId, ct);
            return;
        }
        await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = ObjectKey(key), UploadId = Require(uploadId),
            PartETags = [.. receipts.Select((etag, i) => new PartETag(i + 1, etag))]
        }, ct);
    }

    public async Task AbortUploadAsync(string key, string? uploadId, CancellationToken ct)
    {
        if (uploadId is null) return;
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = ObjectKey(key), UploadId = uploadId }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { } // already gone
    }

    public async Task<BlobContent?> OpenAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = ObjectKey(key) }, ct);
            return new BlobContent(response.ResponseStream, response.ContentLength);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<Uri?> GetDownloadUrlAsync(string key, string fileName, string contentType, bool inline, TimeSpan lifetime, CancellationToken ct)
    {
        var url = await presigner.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = ObjectKey(key),
            Verb = HttpVerb.GET,
            Protocol = publicProtocol,
            Expires = DateTime.UtcNow + lifetime,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = contentType,
                ContentDisposition = ContentDisposition(fileName, inline)
            }
        });
        return new Uri(url);
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = ObjectKey(key) }, ct);

    private string ObjectKey(string key) => prefix + BlobKeys.Require(key);

    private static string Require(string? uploadId) =>
        string.IsNullOrEmpty(uploadId) ? throw new ArgumentException("The upload has no S3 upload id.", nameof(uploadId)) : uploadId;

    /// <summary>attachment or inline, with an ASCII name for old clients and the real one (RFC 5987) for the rest.</summary>
    internal static string ContentDisposition(string fileName, bool inline)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var c in fileName) ascii.Append(c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_');
        return $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private Task EnsureBucketAsync(CancellationToken ct)
    {
        if (!createBucket) return Task.CompletedTask;
        var ready = _bucketReady ??= CreateBucketAsync();
        if (ready.IsFaulted) _bucketReady = null; // try again next time
        return ready.WaitAsync(ct);
    }

    private async Task CreateBucketAsync()
    {
        try { await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }); }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
    }
}
