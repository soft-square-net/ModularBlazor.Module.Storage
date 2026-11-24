using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.Infrastructure.Blobs;

/// <summary>
/// The stores by location (<c>connection|bucket|prefix</c>; empty: the local disk of "Storage:Local"), made on first
/// use and kept, with one S3 client per connection. Disposed with the module's container, which drops the clients.
/// </summary>
internal sealed class BlobStores(IOptions<LocalBlobOptions> local, IOptions<BlobConnectionsOptions> connections) : IBlobStores, IDisposable
{
    private readonly ConcurrentDictionary<string, IBlobStore> _stores = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (AmazonS3Client Client, AmazonS3Client Presigner, Protocol Protocol)> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    public IBlobStore Get(string location) => _stores.GetOrAdd(location ?? "", Create);

    private IBlobStore Create(string location)
    {
        if (location.Length == 0) return new FileSystemBlobStore(local.Value.ResolvedRoot);

        var (name, bucket, prefix) = BlobLocations.Parse(location);
        if (!connections.Value.Connections.TryGetValue(name, out var connection))
            throw new InvalidOperationException($"The storage connection '{name}' is not configured (Storage:Connections:{name}).");

        switch (connection.Kind)
        {
            case BlobConnectionKind.Local:
                if (string.IsNullOrWhiteSpace(connection.Root))
                    throw new InvalidOperationException($"The storage connection '{name}' needs a Root folder.");
                return new FileSystemBlobStore(Path.Combine([connection.Root, .. new[] { bucket, prefix }.Where(p => p.Length > 0)]));
            case BlobConnectionKind.S3:
                if (string.IsNullOrWhiteSpace(bucket)) throw new InvalidOperationException($"No bucket for the storage connection '{name}'.");
                var (client, presigner, protocol) = _clients.GetOrAdd(name, _ => Clients(connection));
                return new S3BlobStore(client, presigner, protocol, bucket, prefix, connection.CreateBucket);
            default:
                throw new InvalidOperationException($"Unknown kind of storage connection '{name}'.");
        }
    }

    private static (AmazonS3Client, AmazonS3Client, Protocol) Clients(BlobConnectionOptions connection)
    {
        var client = Client(connection, connection.ServiceUrl);
        if (string.IsNullOrWhiteSpace(connection.PublicUrl))
            return (client, client, ProtocolOf(connection.ServiceUrl));
        // Presigning is local (no request), so a client for the public endpoint costs nothing.
        return (client, Client(connection, connection.PublicUrl), ProtocolOf(connection.PublicUrl));
    }

    internal static AmazonS3Client Client(BlobConnectionOptions connection, string? serviceUrl)
    {
        var region = string.IsNullOrWhiteSpace(connection.Region) ? "us-east-1" : connection.Region.Trim();
        var config = new AmazonS3Config
        {
            ForcePathStyle = connection.ForcePathStyle,
            // MinIO and other S3 servers do not all take the SDK's default checksums; send and check them only when required.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (string.IsNullOrWhiteSpace(serviceUrl)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        else
        {
            config.ServiceURL = serviceUrl.Trim();
            config.AuthenticationRegion = region;
        }
        return string.IsNullOrWhiteSpace(connection.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(connection.AccessKey, connection.SecretKey), config);
    }

    private static Protocol ProtocolOf(string? url) =>
        url is not null && url.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

    public void Dispose()
    {
        foreach (var (client, presigner, _) in _clients.Values)
        {
            client.Dispose();
            if (!ReferenceEquals(client, presigner)) presigner.Dispose();
        }
        _clients.Clear();
        _stores.Clear();
    }
}
