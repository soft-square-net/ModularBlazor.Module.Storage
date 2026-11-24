using soft.square.net.WebApi.Modules.Storage.Core.Common;

namespace soft.square.net.WebApi.Modules.Storage.Core.UploadAggregate;

/// <summary>What to do when the folder already has an item with the uploaded file's name (Explorer's choices).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NameConflict>))]
public enum NameConflict
{
    /// <summary>Refuse (409); the client asks the user.</summary>
    Fail,
    /// <summary>The upload becomes a new version of the existing file.</summary>
    Replace,
    /// <summary>The upload gets a numbered name: "Report (2).pdf".</summary>
    KeepBoth
}

/// <summary>
/// A resumable upload: the client sends the file in chunks of <see cref="ChunkSize"/> bytes, in order, each at the
/// offset the session has <see cref="Received"/> (a lost response is retried at the offset the session reports), then
/// completes it. The bytes go straight to the provider (a staging file, or an S3 multipart upload); nothing is in the
/// tenant's tree until <c>complete</c>.
/// </summary>
public sealed class UploadSession : AggregateRoot<Guid>, ITenantOwned
{
    public const int PartsLength = 20000;

    /// <summary>Most chunks of one upload (an S3 ETag is about 34 characters; <see cref="Parts"/> holds them all).</summary>
    public const int MaxParts = 500;

    private UploadSession() { } // EF Core

    public string TenantId { get; set; } = "";
    public Guid FolderId { get; private set; }
    public string Name { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long Size { get; private set; }
    public int ChunkSize { get; private set; }
    public long Received { get; private set; }
    public NameConflict Conflict { get; private set; }
    public string BlobKey { get; private set; } = "";
    /// <summary>The store the bytes go to (see <c>Blob.Location</c>; null: the local disk).</summary>
    public string? Location { get; private set; }
    /// <summary>The provider's id of the upload (S3 multipart upload id); null for the local disk.</summary>
    public string? ProviderUploadId { get; private set; }
    /// <summary>The provider's receipts of the parts sent so far (S3 ETags), one per line; null for the local disk.</summary>
    public string? Parts { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int Version { get; private set; }

    public int ChunkCount => Size == 0 ? 0 : (int)((Size + ChunkSize - 1) / ChunkSize);
    public bool IsComplete => Received == Size;

    public static UploadSession Start(Guid folderId, string name, string? contentType, long size, int chunkSize, long maxFileSize,
        NameConflict conflict, string blobKey, string location, string by, DateTimeOffset now, TimeSpan lifetime)
    {
        if (size < 0) throw new DomainException("A size cannot be negative.");
        if (size > maxFileSize) throw new DomainException($"Files can be at most {maxFileSize:N0} bytes.");
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (!Enum.IsDefined(conflict)) throw new DomainException("Unknown conflict choice.");
        return new UploadSession
        {
            Id = Guid.CreateVersion7(now),
            FolderId = folderId,
            Name = ItemNames.Require(name),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            Size = size,
            // Bigger chunks for big files, so that no upload has more than MaxParts.
            ChunkSize = (int)Math.Max(chunkSize, (size + MaxParts - 1) / MaxParts),
            Conflict = conflict,
            BlobKey = blobKey,
            Location = location.Length == 0 ? null : location,
            CreatedBy = by,
            CreatedAt = now,
            ExpiresAt = now + lifetime
        };
    }

    public void UseProviderUpload(string? providerUploadId) => ProviderUploadId = providerUploadId;

    /// <summary>
    /// Checks a chunk before its bytes are written: at the expected offset, a whole chunk (only the last one may be
    /// shorter). Returns its part number (1-based, as S3 counts).
    /// </summary>
    public int CheckChunk(long offset, long length, string by, DateTimeOffset now)
    {
        if (by != CreatedBy) throw new DomainException("Only the user who started an upload can send its chunks.");
        if (now >= ExpiresAt) throw new DomainException("The upload expired; start it again.");
        if (offset != Received) throw new DomainException($"Expected the chunk at offset {Received}.");
        var expected = Math.Min(ChunkSize, Size - Received);
        if (length != expected) throw new DomainException($"Expected a chunk of {expected} bytes.");
        return (int)(offset / ChunkSize) + 1;
    }

    /// <summary>Records a chunk the provider stored, with its receipt (an S3 ETag) if there is one.</summary>
    public void ChunkStored(long length, string? receipt)
    {
        Received += length;
        if (receipt is not null) Parts = Parts is null ? receipt : Parts + "\n" + receipt;
        if (Parts?.Length > PartsLength) throw new DomainException("The upload has too many parts.");
        Version++;
    }

    public IReadOnlyList<string> PartReceipts => Parts?.Split('\n') ?? [];
}

public sealed class ExpiredUploadsSpec : Specification<UploadSession>
{
    public ExpiredUploadsSpec(DateTimeOffset now) => Query.Where(u => u.ExpiresAt <= now).Take(100);
}
