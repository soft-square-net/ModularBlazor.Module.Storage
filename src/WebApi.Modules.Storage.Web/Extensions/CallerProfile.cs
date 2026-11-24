using soft.square.net.WebApi.Modules.Storage.UseCases.Sharing;

namespace soft.square.net.WebApi.Modules.Storage.Web.Extensions;

/// <summary>The caller's name and email from the access token (claims "name" and "email").</summary>
internal sealed class CallerProfile(IHttpContextAccessor http) : ICallerProfile
{
    public string? Name => http.HttpContext?.User.FindFirst(PlatformClaims.Name)?.Value;
    public string? Email => http.HttpContext?.User.FindFirst(PlatformClaims.Email)?.Value;
}
