namespace soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

/// <summary>What a module-signed download link opens: one blob of one tenant, with the name and type to serve it as.</summary>
public sealed record DownloadTicket(string TenantId, string BlobKey, string FileName, string ContentType, bool Inline);

/// <summary>
/// Module-signed download links for providers that cannot presign (the local disk): the ticket is signed into a token
/// that expires, so a browser can load a file (an &lt;img&gt;, a PDF viewer, a plain link) without the access token.
/// Implemented in Web with ASP.NET Core Data Protection, whose keys the replicas share.
/// </summary>
public interface IDownloadTickets
{
    /// <summary>The absolute URL of the module's anonymous download endpoint for this ticket.</summary>
    Uri Link(DownloadTicket ticket, TimeSpan lifetime);

    /// <summary>The ticket, or null when the token is forged or expired.</summary>
    DownloadTicket? Unprotect(string token);
}
