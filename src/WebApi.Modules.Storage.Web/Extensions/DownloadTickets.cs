using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using soft.square.net.WebApi.Modules.Storage.UseCases.Blobs;

namespace soft.square.net.WebApi.Modules.Storage.Web.Extensions;

/// <summary>
/// Module-signed download links: the ticket as JSON, protected with ASP.NET Core Data Protection (time-limited; the
/// replicas share the keys), in the path of the anonymous endpoint <c>GET /api/storage/links/{token}</c>.
/// </summary>
internal sealed class DownloadTickets(IDataProtectionProvider protection, LinkGenerator links, IHttpContextAccessor http) : IDownloadTickets
{
    public const string RouteName = "Storage_OpenLink";

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("soft.square.net.storage.download-links.v1").ToTimeLimitedDataProtector();

    public Uri Link(DownloadTicket ticket, TimeSpan lifetime)
    {
        var token = _protector.Protect(JsonSerializer.Serialize(ticket), lifetime);
        var context = http.HttpContext ?? throw new InvalidOperationException("Download links are made during a request.");
        var url = links.GetUriByName(context, RouteName, new { token })
                  ?? throw new InvalidOperationException($"The route {RouteName} is not mapped.");
        return new Uri(url);
    }

    public DownloadTicket? Unprotect(string token)
    {
        try { return JsonSerializer.Deserialize<DownloadTicket>(_protector.Unprotect(token)); }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }
}
