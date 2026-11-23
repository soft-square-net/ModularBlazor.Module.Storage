using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Items;

/// <summary>A file's bytes to send, with what the response needs.</summary>
public sealed record FileContent(Stream Stream, long Length, string FileName, string ContentType, DateTimeOffset LastModified, string ETag);

/// <summary>
/// Download a file (Read data), the current version or an older one, streamed by the module with ranges. For the
/// browser (an &lt;img&gt;, a video, a PDF viewer, a plain link) <see cref="GetDownloadLinkQuery"/> is better: it needs no
/// access token and, on S3, does not pass through the API.
/// </summary>
public sealed record GetFileContentQuery(Guid Id, int? Version) : IQuery<Result<FileContent>>;

public sealed class GetFileContentHandler(ItemAccess access, IStorageStore store, IBlobStores stores) : IQueryHandler<GetFileContentQuery, Result<FileContent>>
{
    public async ValueTask<Result<FileContent>> Handle(GetFileContentQuery query, CancellationToken ct)
    {
        var version = await FileVersions.ReachAsync(access, query.Id, query.Version, ct);
        if (!version.IsSuccess) return version.As<FileContent>();
        var (item, v) = version.Value;
        var content = await stores.Get(await FileVersions.LocationAsync(store, v, ct)).OpenAsync(v.BlobKey, ct);
        if (content is null) return Result<FileContent>.NotFound("The file's content is missing from storage.");
        return new FileContent(content.Stream, content.Length, item.Name, v.ContentType, v.CreatedAt, $"\"{item.Id:N}-{v.Number}\"");
    }
}

/// <summary>A short-lived link to download (or, with <see cref="Inline"/>, show) a file: presigned by S3, or signed by the module.</summary>
public sealed record GetDownloadLinkQuery(Guid Id, int? Version, bool Inline) : IQuery<Result<DownloadLink>>;

public sealed class GetDownloadLinkHandler(ItemAccess access, IStorageStore store, IBlobStores stores, IDownloadTickets tickets, ITenantContext tenant,
    StorageCaller caller, IOptions<StorageOptions> options) : IQueryHandler<GetDownloadLinkQuery, Result<DownloadLink>>
{
    public async ValueTask<Result<DownloadLink>> Handle(GetDownloadLinkQuery query, CancellationToken ct)
    {
        var version = await FileVersions.ReachAsync(access, query.Id, query.Version, ct);
        if (!version.IsSuccess) return version.As<DownloadLink>();
        var (item, v) = version.Value;
        var lifetime = options.Value.DownloadLinkLifetime;
        var location = await FileVersions.LocationAsync(store, v, ct);
        var url = await stores.Get(location).GetDownloadUrlAsync(v.BlobKey, item.Name, v.ContentType, query.Inline, lifetime, ct)
                  ?? tickets.Link(new DownloadTicket(tenant.Required.Id, v.BlobKey, item.Name, v.ContentType, query.Inline, location), lifetime);
        return new DownloadLink(url, caller.Now + lifetime);
    }
}

/// <summary>Opens what a module-signed link names (anonymous: the signature is the authorization).</summary>
public sealed record OpenTicketQuery(string Token) : IQuery<Result<(DownloadTicket Ticket, BlobContent Content)>>;

public sealed class OpenTicketHandler(IDownloadTickets tickets, IBlobStores stores, ITenantContext tenant)
    : IQueryHandler<OpenTicketQuery, Result<(DownloadTicket Ticket, BlobContent Content)>>
{
    public async ValueTask<Result<(DownloadTicket Ticket, BlobContent Content)>> Handle(OpenTicketQuery query, CancellationToken ct)
    {
        var ticket = tickets.Unprotect(query.Token);
        // A tenant named by the request (host name, header) must be the ticket's; keys always start with their tenant.
        if (ticket is null
            || (tenant.TenantId is { } requested && requested != ticket.TenantId)
            || !ticket.BlobKey.StartsWith($"t/{ticket.TenantId}/", StringComparison.Ordinal))
            return Result.NotFound("The link is invalid or has expired.");
        var content = await stores.Get(ticket.Location ?? "").OpenAsync(ticket.BlobKey, ct);
        return content is null ? Result.NotFound("The file's content is missing from storage.") : (ticket, content);
    }
}

internal static class FileVersions
{
    /// <summary>The store a version's blob was written to (a blob without a record is from before locations: the local disk).</summary>
    public static async Task<string> LocationAsync(IStorageStore store, FileVersion version, CancellationToken ct) =>
        (await store.BlobsAsync([version.BlobKey], ct)).FirstOrDefault()?.Location ?? "";

    /// <summary>A file the caller may read (Read data), and the version asked for (the current one by default).</summary>
    public static async Task<Result<(Item Item, FileVersion Version)>> ReachAsync(ItemAccess access, Guid id, int? number, CancellationToken ct)
    {
        var reached = await access.ReachAsync(id, StorageRights.ListFolder, ct);
        if (!reached.IsSuccess) return reached.As<(Item, FileVersion)>();
        var item = reached.Value.Item;
        if (item.IsFolder) return Result<(Item, FileVersion)>.Invalid(new ValidationError("A folder has no content to download."));
        var version = item.Versions.FirstOrDefault(v => v.Number == (number ?? item.CurrentVersion));
        return version is null ? Result<(Item, FileVersion)>.NotFound($"'{item.Name}' has no version {number}.") : (item, version);
    }
}
