namespace soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;

/// <summary>One version of a file: its bytes are the blob <see cref="BlobKey"/> (shared by copies of the file).</summary>
public sealed class FileVersion
{
    public const int BlobKeyLength = 200;

    private FileVersion() { } // EF Core

    public int Number { get; private set; }
    public string BlobKey { get; private set; } = "";
    public long Size { get; private set; }
    public string ContentType { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static FileVersion Create(string blobKey, long size, string? contentType, string by, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(blobKey) || blobKey.Length > BlobKeyLength) throw new DomainException("A version needs a blob.");
        if (size < 0) throw new DomainException("A size cannot be negative.");
        return new FileVersion
        {
            BlobKey = blobKey,
            Size = size,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            CreatedAt = now,
            CreatedBy = by
        };
    }

    internal FileVersion Numbered(int number) => new()
    {
        Number = number, BlobKey = BlobKey, Size = Size, ContentType = ContentType, CreatedAt = CreatedAt, CreatedBy = CreatedBy
    };
}
