namespace soft.square.net.WebApi.Modules.Storage.Core.BlobAggregate;

/// <summary>
/// Bytes in the storage provider, by an immutable key (<c>t/{tenant}/{yyyy}/{MM}/{id}</c>). Copies of a file and its
/// versions share blobs, so a blob counts the versions that use it; the garbage collector removes the ones nobody
/// uses after the recycle bin is emptied.
/// </summary>
public sealed class Blob : AggregateRoot<string>, ITenantOwned
{
    private Blob() { } // EF Core

    public const int LocationLength = 300;

    public string TenantId { get; set; } = "";
    /// <summary>The store the bytes were written to (<c>connection|bucket|prefix</c>; null: the local disk).</summary>
    public string? Location { get; private set; }
    public long Size { get; private set; }
    public int References { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Blob Create(string key, string location, long size, DateTimeOffset now) =>
        new() { Id = key, Location = location.Length == 0 ? null : location, Size = size, References = 1, CreatedAt = now };

    public void AddReference() => References++;

    public void Release() => References = Math.Max(0, References - 1);

    /// <summary>A new key for a tenant's blob; never derived from a file name.</summary>
    public static string NewKey(string tenantId, DateTimeOffset now) =>
        $"t/{tenantId}/{now.UtcDateTime:yyyy}/{now.UtcDateTime:MM}/{Guid.CreateVersion7(now):N}";
}
