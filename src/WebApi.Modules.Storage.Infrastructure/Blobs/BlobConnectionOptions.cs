namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

public enum BlobConnectionKind
{
    /// <summary>Amazon S3, or anything that speaks its API (MinIO, Ceph, R2, ...).</summary>
    S3,
    /// <summary>A folder (every replica must see the same one): blobs under Root/bucket/prefix.</summary>
    Local
}

/// <summary>
/// A named connection (section "Storage:Connections:{name}"). Keys and secrets belong in the host's settings, not in
/// the module's catalog record: the host override <c>Modules:Settings:storage:Storage:Connections:{name}:SecretKey</c>
/// (an environment variable, user secrets, a vault) reaches the module and is never written to modules.json.
/// </summary>
public sealed class BlobConnectionOptions
{
    public BlobConnectionKind Kind { get; set; } = BlobConnectionKind.S3;

    /// <summary>S3: the endpoint (MinIO: http://minio:9000); empty for Amazon S3 itself, which uses <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>S3: the region (default us-east-1; MinIO accepts any).</summary>
    public string? Region { get; set; }

    /// <summary>S3: the access key; empty uses the default AWS credentials (environment, profile, instance role).</summary>
    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>S3: bucket in the path (http://host/bucket/key), as MinIO needs; Amazon S3 uses the host name.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// S3: the endpoint browsers reach, when it differs from <see cref="ServiceUrl"/> (the API talks to http://minio:9000,
    /// browsers download from https://files.example.com). Download links are presigned for this one.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>S3: create a missing bucket on first use (development; production buckets are made by their owner).</summary>
    public bool CreateBucket { get; set; }

    /// <summary>Local: the folder.</summary>
    public string? Root { get; set; }
}

/// <summary>The named connections (section "Storage").</summary>
public sealed class BlobConnectionsOptions
{
    public Dictionary<string, BlobConnectionOptions> Connections { get; } = new(StringComparer.OrdinalIgnoreCase);
}
