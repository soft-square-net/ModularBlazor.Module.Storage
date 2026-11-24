namespace soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

/// <summary>
/// The blob stores, by location. A location names a connection, a bucket and a prefix
/// (<c>connection|bucket|prefix</c>); the empty location is the local disk of "Storage:Local", where every blob of
/// phase 1 lives. Blobs and uploads keep the location they were written to.
/// </summary>
public interface IBlobStores
{
    /// <summary>The store of a location; throws when its connection is not configured (any more).</summary>
    IBlobStore Get(string location);
}

public static class BlobLocations
{
    public const int MaxLength = 300;

    /// <summary>
    /// The connection name of the local disk of "Storage:Local" (reserved): a tenant whose default is a bucket keeps
    /// its files on the disk with <c>Storage:Tenants:{tenant}:Connection = local</c>.
    /// </summary>
    public const string LocalDisk = "local";

    /// <summary>The location a placement writes to.</summary>
    public static string Of(BlobPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(placement.Connection) || placement.Connection.Trim().Equals(LocalDisk, StringComparison.OrdinalIgnoreCase))
            return "";
        var location = $"{placement.Connection.Trim()}|{placement.Bucket?.Trim()}|{NormalizePrefix(placement.Prefix)}";
        return location.Length <= MaxLength ? location : throw new InvalidOperationException("The storage location is too long.");
    }

    /// <summary>The parts of a location; the empty location has no connection.</summary>
    public static (string Connection, string Bucket, string Prefix) Parse(string location)
    {
        if (string.IsNullOrEmpty(location)) return ("", "", "");
        var parts = location.Split('|');
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : throw new InvalidOperationException($"Invalid storage location '{location}'.");
    }

    /// <summary>"" or "a/b/" (no leading slash, one trailing slash).</summary>
    public static string NormalizePrefix(string? prefix)
    {
        var p = (prefix ?? "").Replace('\\', '/').Trim().Trim('/');
        return p.Length == 0 ? "" : p + "/";
    }
}
